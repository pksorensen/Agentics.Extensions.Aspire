using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using Aspire.Hosting.AzureResourceManager;

namespace Aspire.Hosting.Foundry.Emulator;

/// <summary>
/// <c>Microsoft.CognitiveServices</c> as far as a Foundry consumer reads it:
/// accounts (list by subscription and resource group, get, <c>listKeys</c>),
/// deployments (list, get, put, delete), account and regional <c>usages</c>
/// (quota consumption from deployment capacity), <c>models</c> and <c>projects</c>.
/// Any dated api-version is accepted — the SDK defaults move faster than an
/// emulator should be released.
/// </summary>
public sealed class CognitiveServicesArmProvider(FoundryState state) : IArmProviderEmulator
{
    internal static readonly Regex ApiVersion = new(@"^\d{4}-\d{2}-\d{2}(-preview)?$", RegexOptions.Compiled);

    public ValueTask<ArmProviderResponse?> HandleAsync(ArmProviderRequest request, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Handle(request));

    private ArmProviderResponse? Handle(ArmProviderRequest request)
    {
        if (!ApiVersion.IsMatch(request.ApiVersion ?? ""))
            return ArmProviderResponse.InvalidApiVersion(request.ProviderNamespace, request.ApiVersion);

        var segments = request.ResourcePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var scope = ArmScope.Parse(request.ResourceId);

        if (segments.Length >= 3 && segments[0].Equals("locations", StringComparison.OrdinalIgnoreCase) &&
            segments[2].Equals("usages", StringComparison.OrdinalIgnoreCase) && request.Method == "GET")
            return ArmProviderResponse.Ok(new
            {
                value = Usages(state.Accounts.Where(a => a.SubscriptionId.Equals(scope.SubscriptionId, StringComparison.OrdinalIgnoreCase)
                                                        && a.Location.Equals(segments[1], StringComparison.OrdinalIgnoreCase))),
            });

        if (segments.Length == 0 || !segments[0].Equals("accounts", StringComparison.OrdinalIgnoreCase)) return null;

        if (segments.Length == 1)
        {
            if (request.Method != "GET") return ArmProviderResponse.NotFound($"Method {request.Method} is not emulated for account collections.");
            var accounts = state.Accounts.Where(a =>
                a.SubscriptionId.Equals(scope.SubscriptionId, StringComparison.OrdinalIgnoreCase) &&
                (scope.ResourceGroup is null || a.ResourceGroup.Equals(scope.ResourceGroup, StringComparison.OrdinalIgnoreCase)));
            return ArmProviderResponse.Ok(new { value = accounts.Select(Account) });
        }

        var account = state.FindAccount(segments[1]);
        if (account is null || !account.SubscriptionId.Equals(scope.SubscriptionId, StringComparison.OrdinalIgnoreCase) ||
            (scope.ResourceGroup is not null && !account.ResourceGroup.Equals(scope.ResourceGroup, StringComparison.OrdinalIgnoreCase)))
            return ArmProviderResponse.NotFound($"The Resource 'Microsoft.CognitiveServices/accounts/{segments[1]}' under resource group '{scope.ResourceGroup}' was not found.");

        if (segments.Length == 2)
            return request.Method == "GET" ? ArmProviderResponse.Ok(Account(account)) : ArmProviderResponse.NotFound($"Method {request.Method} is not emulated for accounts.");

        var child = segments[2].ToLowerInvariant();
        switch (child)
        {
            case "listkeys" when request.Method == "POST":
                var keys = account.Keys.Values.Where(k => k.Name is "key1" or "key2").OrderBy(k => k.Name).ToArray();
                return ArmProviderResponse.Ok(new { key1 = keys.ElementAtOrDefault(0)?.Value, key2 = keys.ElementAtOrDefault(1)?.Value });

            case "usages" when request.Method == "GET":
                return ArmProviderResponse.Ok(new { value = Usages([account]) });

            case "models" when request.Method == "GET":
                return ArmProviderResponse.Ok(new
                {
                    value = account.Deployments.Values.GroupBy(d => (d.ModelFormat, d.ModelName, d.ModelVersion)).Select(g => new
                    {
                        kind = account.Kind,
                        skuName = "S0",
                        model = new
                        {
                            format = g.Key.ModelFormat,
                            name = g.Key.ModelName,
                            version = g.Key.ModelVersion,
                            capabilities = Capabilities(g.First()),
                            lifecycleStatus = "GenerallyAvailable",
                            skus = g.Select(d => d.SkuName).Distinct().Select(s => new { name = s, usageName = $"OpenAI.{s}.{g.Key.ModelName}", capacity = new { maximum = 10000, @default = 100 } }),
                        },
                    }),
                });

            case "projects" when request.Method == "GET":
                if (segments.Length == 3) return ArmProviderResponse.Ok(new { value = account.Projects.Select(p => Project(account, p)) });
                return account.Projects.Contains(segments[3], StringComparer.OrdinalIgnoreCase)
                    ? ArmProviderResponse.Ok(Project(account, segments[3]))
                    : ArmProviderResponse.NotFound($"Project '{segments[3]}' was not found.");

            case "deployments":
                return Deployments(request, account, segments);
        }

        return null;
    }

    private ArmProviderResponse Deployments(ArmProviderRequest request, FoundryAccount account, string[] segments)
    {
        if (segments.Length == 3)
            return request.Method == "GET"
                ? ArmProviderResponse.Ok(new { value = account.Deployments.Values.OrderBy(d => d.Name).Select(d => Deployment(account, d)) })
                : ArmProviderResponse.NotFound($"Method {request.Method} is not emulated for the deployments collection.");

        var name = segments[3];
        switch (request.Method)
        {
            case "GET":
                return account.Deployments.TryGetValue(name, out var existing)
                    ? ArmProviderResponse.Ok(Deployment(account, existing))
                    : ArmProviderResponse.NotFound($"Deployment '{name}' was not found.");
            case "PUT" when request.Body is { } body:
                var model = body.TryGetProperty("properties", out var p) && p.TryGetProperty("model", out var m) ? m : default;
                var sku = body.TryGetProperty("sku", out var s) ? s : default;
                var created = !account.Deployments.ContainsKey(name);
                var seed = new FoundryDeploymentSeed(
                    name,
                    Str(model, "name") ?? name,
                    Str(model, "version") ?? "1",
                    Str(model, "format") ?? "OpenAI",
                    Str(sku, "name") ?? "GlobalStandard",
                    sku.ValueKind == JsonValueKind.Object && sku.TryGetProperty("capacity", out var c) && c.TryGetInt32(out var cap) ? cap : 10,
                    account.Deployments.TryGetValue(name, out var previous) ? previous.Scenario : null,
                    body.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Object
                        ? tags.EnumerateObject().ToDictionary(t => t.Name, t => t.Value.ToString())
                        : previous?.Tags,
                    previous?.Aliases);
                account.Deployments[name] = seed;
                return created ? ArmProviderResponse.Created(Deployment(account, seed)) : ArmProviderResponse.Ok(Deployment(account, seed));
            case "DELETE":
                return account.Deployments.TryRemove(name, out _) ? ArmProviderResponse.Ok(new { }) : ArmProviderResponse.NoContent();
        }
        return ArmProviderResponse.NotFound($"Method {request.Method} is not emulated for deployments.");
    }

    private static string? Str(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private object Account(FoundryAccount a) => new
    {
        id = a.ResourceId,
        name = a.Name,
        type = "Microsoft.CognitiveServices/accounts",
        location = a.Location,
        kind = a.Kind,
        tags = a.Tags,
        sku = new { name = "S0" },
        identity = new { type = "SystemAssigned" },
        properties = new
        {
            provisioningState = "Succeeded",
            endpoint = $"{state.PublicUrl ?? "http://localhost"}/{a.Name}/",
            endpoints = new Dictionary<string, string>
            {
                ["OpenAI Language Model Instance API"] = $"{state.PublicUrl ?? "http://localhost"}/{a.Name}/",
                ["AI Foundry API"] = $"{state.PublicUrl ?? "http://localhost"}/{a.Name}/",
            },
            customSubDomainName = a.Name,
            publicNetworkAccess = "Enabled",
            disableLocalAuth = false,
            allowProjectManagement = a.Kind == "AIServices",
        },
    };

    private static object Project(FoundryAccount a, string project) => new
    {
        id = $"{a.ResourceId}/projects/{project}",
        name = project,
        type = "Microsoft.CognitiveServices/accounts/projects",
        location = a.Location,
        properties = new { provisioningState = "Succeeded", displayName = project, isDefault = a.Projects.FirstOrDefault() == project },
    };

    internal static Dictionary<string, string> Capabilities(FoundryDeploymentSeed d)
    {
        var embeddings = d.ModelName.Contains("embedding", StringComparison.OrdinalIgnoreCase);
        return embeddings
            ? new() { ["embeddings"] = "true" }
            : new() { ["chatCompletion"] = "true", ["responses"] = "true", ["agentsV2"] = "true", ["maxContextToken"] = "400000", ["maxOutputToken"] = "128000" };
    }

    private static object Deployment(FoundryAccount a, FoundryDeploymentSeed d) => new
    {
        id = $"{a.ResourceId}/deployments/{d.Name}",
        name = d.Name,
        type = "Microsoft.CognitiveServices/accounts/deployments",
        tags = d.Tags,
        sku = new { name = d.SkuName, capacity = d.Capacity },
        properties = new
        {
            model = new { format = d.ModelFormat, name = d.ModelName, version = d.ModelVersion },
            versionUpgradeOption = "OnceNewDefaultVersionAvailable",
            currentCapacity = d.Capacity,
            capabilities = Capabilities(d),
            provisioningState = "Succeeded",
            raiPolicyName = "Microsoft.DefaultV2",
            rateLimits = new object[]
            {
                new { key = "request", renewalPeriod = 60, count = d.Capacity },
                new { key = "token", renewalPeriod = 60, count = d.Capacity * 1000 },
            },
        },
    };

    /// <summary>Quota consumption the way the real API reports it: per
    /// <c>OpenAI.{sku}.{model}</c>, current value = summed deployment capacity (in
    /// units of 1,000 TPM), limit = a generous fixed default.</summary>
    private static IEnumerable<object> Usages(IEnumerable<FoundryAccount> accounts) =>
        accounts.SelectMany(a => a.Deployments.Values)
            .GroupBy(d => $"OpenAI.{d.SkuName}.{d.ModelName}", StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new
            {
                name = new { value = g.Key, localizedValue = $"Tokens Per Minute (thousands) - {g.First().ModelName} - {g.First().SkuName}" },
                currentValue = (double)g.Sum(d => d.Capacity),
                limit = 5000.0,
                unit = "Count",
                quotaPeriod = (string?)null,
                nextResetTime = (string?)null,
                status = "Included",
            });
}

/// <summary>
/// <c>Microsoft.Insights/metrics</c> for a Cognitive Services account, computed
/// from the usage log: <c>InputTokens</c>, <c>OutputTokens</c>, <c>TotalTokens</c>,
/// <c>ProcessedPromptTokens</c>, <c>GeneratedTokens</c>, <c>cacheReadInputTokens</c>
/// and <c>AzureOpenAIRequests</c>, with the <c>ModelDeploymentName</c>,
/// <c>ModelName</c> and <c>StatusCode</c> dimensions honoured in <c>$filter</c>.
/// </summary>
public sealed class FoundryMetricsArmProvider(FoundryState state, FoundryUsageStore usage) : IArmProviderEmulator
{
    private static readonly string[] Dimensions = ["ModelDeploymentName", "ModelName", "StatusCode", "ModelVersion", "ApiName", "Region"];

    public ValueTask<ArmProviderResponse?> HandleAsync(ArmProviderRequest request, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Handle(request));

    private ArmProviderResponse? Handle(ArmProviderRequest request)
    {
        if (request.Method != "GET" || !request.ResourcePath.Equals("metrics", StringComparison.OrdinalIgnoreCase)) return null;
        var marker = request.ResourceId.IndexOf("/providers/Microsoft.CognitiveServices/accounts/", StringComparison.OrdinalIgnoreCase);
        if (marker < 0) return null;
        var accountName = request.ResourceId[(marker + "/providers/Microsoft.CognitiveServices/accounts/".Length)..].Split('/')[0];
        var account = state.FindAccount(accountName);
        if (account is null) return ArmProviderResponse.NotFound($"Account '{accountName}' was not found.");
        var resourceUri = account.ResourceId;
        var query = request.Query ?? new Dictionary<string, string>();

        var now = DateTimeOffset.UtcNow;
        var (from, to) = (now.AddHours(-1), now);
        if (query.TryGetValue("timespan", out var timespan) && timespan.Split('/') is { Length: 2 } parts &&
            DateTimeOffset.TryParse(parts[0], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var f) &&
            DateTimeOffset.TryParse(parts[1], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t))
            (from, to) = (f, t);

        var intervalText = query.TryGetValue("interval", out var i) && i.Length > 0 ? i : "PT1M";
        TimeSpan interval;
        try { interval = intervalText.Equals("FULL", StringComparison.OrdinalIgnoreCase) ? to - from : XmlConvert.ToTimeSpan(intervalText); }
        catch (FormatException) { interval = TimeSpan.FromHours(1); }
        if (interval <= TimeSpan.Zero) interval = TimeSpan.FromHours(1);

        var filters = ParseFilter(query.TryGetValue("$filter", out var filter) ? filter : null);
        var names = (query.TryGetValue("metricnames", out var metricnames) && metricnames.Length > 0 ? metricnames : "TotalTokens")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var records = usage.Snapshot().Where(r => r.Account.Equals(account.Name, StringComparison.OrdinalIgnoreCase) &&
                                                  r.Timestamp >= from && r.Timestamp < to).ToList();
        foreach (var (dimension, wanted) in filters.Where(x => x.Value != "*"))
            records = records.Where(r => DimensionValue(r, dimension, account).Equals(wanted, StringComparison.OrdinalIgnoreCase)).ToList();
        var split = filters.Keys.ToList();

        var value = names.Select(name => new
        {
            id = $"{resourceUri}/providers/Microsoft.Insights/metrics/{name}",
            type = "Microsoft.Insights/metrics",
            name = new { value = name, localizedValue = name },
            displayDescription = $"{name} (Foundry emulator)",
            unit = "Count",
            timeseries = records
                .GroupBy(r => string.Join("|", split.Select(d => DimensionValue(r, d, account))))
                .Select(g => new
                {
                    metadatavalues = split.Select(d => new
                    {
                        name = new { value = d.ToLowerInvariant(), localizedValue = d.ToLowerInvariant() },
                        value = DimensionValue(g.First(), d, account),
                    }),
                    data = Buckets(from, to, interval).Select(bucket => new
                    {
                        timeStamp = bucket.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
                        total = (double)g.Where(r => r.Timestamp >= bucket && r.Timestamp < bucket + interval).Sum(r => Measure(r, name)),
                    }),
                }),
            errorCode = "Success",
        });

        return ArmProviderResponse.Ok(new
        {
            cost = 0,
            timespan = $"{from:yyyy-MM-ddTHH:mm:ssZ}/{to:yyyy-MM-ddTHH:mm:ssZ}",
            interval = intervalText,
            value,
            @namespace = "microsoft.cognitiveservices/accounts",
            resourceregion = account.Location,
        });
    }

    private static IEnumerable<DateTimeOffset> Buckets(DateTimeOffset from, DateTimeOffset to, TimeSpan interval)
    {
        var start = interval >= TimeSpan.FromDays(1) ? new DateTimeOffset(from.UtcDateTime.Date, TimeSpan.Zero) : from;
        for (var b = start; b < to && b < start.AddDays(400); b += interval) yield return b;
    }

    private static long Measure(FoundryUsageRecord r, string metric) => metric.ToLowerInvariant() switch
    {
        "inputtokens" or "processedprompttokens" => r.InputTokens,
        "outputtokens" or "generatedtokens" => r.OutputTokens,
        "totaltokens" or "tokentransaction" => r.TotalTokens,
        "cachereadinputtokens" => r.CachedInputTokens,
        "azureopenairequests" or "modelrequests" => r.Requests,
        _ => 0,
    };

    private static string DimensionValue(FoundryUsageRecord r, string dimension, FoundryAccount account) => dimension.ToLowerInvariant() switch
    {
        "modeldeploymentname" or "deploymentname" => r.Deployment,
        "modelname" => r.Model,
        "statuscode" => r.StatusCode.ToString(CultureInfo.InvariantCulture),
        "modelversion" => account.Deployments.TryGetValue(r.Deployment, out var d) ? d.ModelVersion : "",
        "apiname" => r.Operation,
        "region" => account.Location,
        _ => "",
    };

    /// <summary>The subset of the metrics <c>$filter</c> grammar real callers use:
    /// <c>A eq 'x' and B eq '*'</c>.</summary>
    internal static Dictionary<string, string> ParseFilter(string? filter)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(filter)) return result;
        foreach (Match m in Regex.Matches(filter, @"(\w+)\s+eq\s+'([^']*)'", RegexOptions.IgnoreCase))
        {
            var dimension = Dimensions.FirstOrDefault(d => d.Equals(m.Groups[1].Value, StringComparison.OrdinalIgnoreCase)) ?? m.Groups[1].Value;
            result[dimension] = m.Groups[2].Value;
        }
        return result;
    }
}

internal readonly record struct ArmScope(string? SubscriptionId, string? ResourceGroup)
{
    public static ArmScope Parse(string resourceId)
    {
        var parts = resourceId.Split('/', StringSplitOptions.RemoveEmptyEntries);
        string? After(string marker)
        {
            var index = Array.FindIndex(parts, p => p.Equals(marker, StringComparison.OrdinalIgnoreCase));
            return index >= 0 && index + 1 < parts.Length ? parts[index + 1] : null;
        }
        return new ArmScope(After("subscriptions"), After("resourceGroups"));
    }
}
