using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Aspire.Hosting.AzureResourceManager.Emulator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Foundry.Emulator;

public static class FoundryEmulatorExtensions
{
    public const string SeedKey = "FOUNDRY_EMULATOR_JSON";

    /// <summary>Registers the emulator's services from <c>FOUNDRY_EMULATOR_JSON</c>
    /// (written by the hosting package), <c>FOUNDRY_SCENARIOS_DIR</c>,
    /// <c>FOUNDRY_TOKENINFO_URL</c> and <c>FOUNDRY_DATA_DIR</c>.</summary>
    public static IServiceCollection AddFoundryEmulator(this IServiceCollection services, IConfiguration configuration)
    {
        var json = configuration[SeedKey];
        var seed = string.IsNullOrWhiteSpace(json)
            ? new FoundrySeed([])
            : JsonSerializer.Deserialize<FoundrySeed>(json) ?? throw new InvalidOperationException($"{SeedKey} is invalid.");

        var behaviour = seed.Behaviour ?? new FoundryBehaviour();
        behaviour = behaviour with
        {
            TokenInfoUrl = configuration["FOUNDRY_TOKENINFO_URL"] is { Length: > 0 } tokenInfo ? tokenInfo : behaviour.TokenInfoUrl,
            ScenarioDirectory = configuration["FOUNDRY_SCENARIOS_DIR"] is { Length: > 0 } dir ? dir : behaviour.ScenarioDirectory,
        };
        seed = seed with { Behaviour = behaviour };

        services.AddHttpClient("foundry-tokeninfo", c => c.Timeout = TimeSpan.FromSeconds(10));
        services.AddSingleton(seed);
        services.AddSingleton(sp => new FoundryState(seed, sp.GetRequiredService<IConfiguration>()));
        services.AddSingleton(sp =>
        {
            var store = new FoundryUsageStore(configuration["FOUNDRY_DATA_DIR"]);
            var state = sp.GetRequiredService<FoundryState>();
            var now = DateTimeOffset.UtcNow;
            foreach (var entry in seed.Usage ?? [])
            {
                var model = state.FindAccount(entry.Account)?.FindDeployment(entry.Deployment)?.ModelName ?? entry.Deployment;
                store.AddSeeded(FoundryUsageStore.Generate(entry, model, now));
            }
            return store;
        });
        services.AddSingleton(new FoundryScenarioStore(behaviour.ScenarioDirectory));
        services.AddSingleton<FoundryPlayback>();
        services.AddSingleton<FoundryResponseCache>();
        services.AddSingleton<FoundryAuthenticator>();

        // ARM: this host answers /arm/... for the two namespaces it owns. Hosting the
        // router here rather than in the tenant emulator keeps metrics next to the
        // usage log they are computed from.
        services.RegisterArmProviderEmulator<CognitiveServicesArmProvider>("Microsoft.CognitiveServices");
        services.RegisterArmProviderEmulator<FoundryMetricsArmProvider>("Microsoft.Insights");
        return services;
    }

    public static IEndpointRouteBuilder MapFoundryEmulator(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/healthz", (FoundryState state, FoundryScenarioStore scenarios) => Results.Ok(new
        {
            status = "ok",
            accounts = state.Accounts.Select(a => a.Name),
            scenarios = scenarios.All.Count,
            scenarioErrors = scenarios.LoadErrors,
        }));

        FoundryDataPlane.Map(endpoints);
        FoundryMcp.Map(endpoints);
        endpoints.MapAzureResourceManagerEmulator();
        MapControlPlane(endpoints.MapGroup("/_emulator"));
        return endpoints;
    }

    /// <summary>The emulator's own API — the things no real Foundry endpoint
    /// offers but a demo or a test needs: read the usage log, mint a key for a
    /// customer, upload a scenario. Unauthenticated: it is a local emulator.</summary>
    private static void MapControlPlane(RouteGroupBuilder api)
    {
        api.MapGet("/accounts", (FoundryState state, HttpRequest request) => state.Accounts.Select(a => new
        {
            a.Name, a.Kind, a.Location, a.SubscriptionId, a.ResourceGroup, a.ResourceId, a.Aliases, a.Projects,
            endpoint = $"{state.BaseUrl(request)}/{a.Name}",
            openAiV1 = $"{state.BaseUrl(request)}/{a.Name}/openai/v1",
            deployments = a.Deployments.Values.OrderBy(d => d.Name).Select(d => new
            {
                d.Name, model = d.ModelName, version = d.ModelVersion, format = d.ModelFormat, sku = d.SkuName, d.Capacity, d.Scenario, d.Aliases,
            }),
        }));

        // ---- usage ----
        api.MapGet("/usage/records", (HttpRequest request, FoundryUsageStore usage) =>
            Results.Ok(Query(request).Apply(usage.Snapshot())));

        api.MapGet("/usage", (HttpRequest request, FoundryUsageStore usage) =>
        {
            var groupBy = (request.Query["groupBy"].ToString() is { Length: > 0 } g ? g : "day,customer,model")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(x => x.ToLowerInvariant()).ToArray();
            var rows = Query(request).Apply(usage.Snapshot())
                .GroupBy(r => string.Join("\u001f", groupBy.Select(k => Key(r, k))))
                .Select(grp =>
                {
                    var first = grp.First();
                    var row = new Dictionary<string, object?>();
                    foreach (var k in groupBy) row[Column(k)] = Key(first, k);
                    // Field names match engx's GET /foundry/gateway/me/usage rows.
                    row["promptTokens"] = grp.Sum(r => r.InputTokens);
                    row["cachedPromptTokens"] = grp.Sum(r => r.CachedInputTokens);
                    row["completionTokens"] = grp.Sum(r => r.OutputTokens);
                    row["reasoningTokens"] = grp.Sum(r => r.ReasoningTokens);
                    row["totalTokens"] = grp.Sum(r => r.TotalTokens);
                    row["requests"] = grp.Sum(r => r.Requests);
                    return row;
                })
                .OrderBy(r => r.TryGetValue("day", out var d) ? d as string : "", StringComparer.Ordinal)
                .ToList();
            return Results.Ok(new { groupBy, rows });
        });

        // AppMetrics-shaped rows, as APIM's llm-emit-token-metric would have written them.
        api.MapGet("/usage/appmetrics", (HttpRequest request, FoundryUsageStore usage) =>
        {
            var rows = Query(request).Apply(usage.Snapshot())
                .GroupBy(r => (Day: r.Timestamp.UtcDateTime.Date, Product: r.Customer ?? "", Subscription: r.SubscriptionId ?? r.Caller, Model: r.Deployment))
                .SelectMany(g => new (string Name, long Sum)[]
                {
                    ("Prompt Tokens", g.Sum(r => r.InputTokens)),
                    ("Prompt Cached Tokens", g.Sum(r => r.CachedInputTokens)),
                    ("Completion Tokens", g.Sum(r => r.OutputTokens)),
                    ("Total Tokens", g.Sum(r => r.TotalTokens)),
                }.Select(m => new
                {
                    TimeGenerated = new DateTimeOffset(g.Key.Day, TimeSpan.Zero),
                    m.Name,
                    m.Sum,
                    Properties = new { Product = g.Key.Product, Subscription = g.Key.Subscription, Model = g.Key.Model },
                }))
                .OrderBy(r => r.TimeGenerated).ThenBy(r => r.Name, StringComparer.Ordinal);
            return Results.Ok(rows);
        });

        api.MapDelete("/usage", (HttpRequest request, FoundryUsageStore usage) =>
            Results.Ok(new { removed = usage.Clear(request.Query["includeSeeded"] == "true") }));

        // ---- keys / gateway subscriptions ----
        api.MapGet("/keys", (FoundryState state) => state.GatewayKeys.Values.OrderBy(k => k.Name).Select(k => new
        {
            k.Name, k.Customer, k.Project, k.SubscriptionId, k.UserPrincipal, k.Initiative, k.Context,
        }));

        api.MapPost("/keys", async (HttpRequest request, FoundryState state) =>
        {
            var body = await JsonSerializer.DeserializeAsync<CreateKeyRequest>(request.Body, FoundryScenarioStore.Json) ?? new CreateKeyRequest();
            if (string.IsNullOrWhiteSpace(body.Customer))
                return Results.BadRequest(new { error = new { code = "invalid_request", message = "'customer' is required." } });
            var upnLocal = body.UserPrincipal?.Split('@')[0];
            var subscription = body.SubscriptionId
                ?? string.Join("-", new[] { body.Customer, body.Initiative is null ? null : "-" + body.Initiative, upnLocal }.Where(x => !string.IsNullOrEmpty(x)));
            var key = body.Key ?? Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
            var seed = new FoundryApiKeySeed(body.Name ?? subscription, key, body.Customer, body.Project, subscription, body.UserPrincipal, body.Initiative, body.Context);
            state.GatewayKeys[key] = seed;
            return Results.Ok(new { name = seed.Name, subscriptionId = subscription, primaryKey = key, customer = seed.Customer, userPrincipal = seed.UserPrincipal });
        });

        api.MapDelete("/keys/{subscriptionId}", (string subscriptionId, FoundryState state) =>
        {
            var removed = state.GatewayKeys.Where(k => string.Equals(k.Value.SubscriptionId, subscriptionId, StringComparison.OrdinalIgnoreCase))
                .Count(k => state.GatewayKeys.TryRemove(k.Key, out _));
            return removed == 0 ? Results.NotFound() : Results.Ok(new { removed });
        });

        api.MapPut("/callers/{callerId}", async (string callerId, HttpRequest request, FoundryState state) =>
        {
            var body = await JsonSerializer.DeserializeAsync<CreateKeyRequest>(request.Body, FoundryScenarioStore.Json) ?? new CreateKeyRequest();
            state.PutCaller(new FoundryCallerSeed(callerId, body.Customer, body.Project, body.Name));
            return Results.Ok(state.FindCaller(callerId));
        });

        // ---- scenarios ----
        api.MapGet("/scenarios", (FoundryScenarioStore scenarios) => Results.Ok(new
        {
            directory = scenarios.Directory,
            errors = scenarios.LoadErrors,
            scenarios = scenarios.All.Select(s => new { s.Name, s.Description, trigger = s.Trigger?.PromptRegex, steps = s.Steps.Count }),
        }));
        api.MapGet("/scenarios/{name}", (string name, FoundryScenarioStore scenarios) =>
            scenarios.Find(name) is { } s ? Results.Json(s, FoundryScenarioStore.Json) : Results.NotFound());
        api.MapPut("/scenarios/{name}", async (string name, HttpRequest request, FoundryScenarioStore scenarios) =>
        {
            using var reader = new StreamReader(request.Body);
            var text = await reader.ReadToEndAsync();
            var isYaml = request.ContentType?.Contains("yaml", StringComparison.OrdinalIgnoreCase) == true;
            FoundryScenario scenario;
            try { scenario = FoundryScenarioStore.Parse(text, isYaml ? "upload.yaml" : "upload.json"); }
            catch (Exception error) { return Results.BadRequest(new { error = new { code = "invalid_scenario", message = error.Message } }); }
            scenario.Name = name;
            scenarios.Put(scenario);
            return Results.Ok(new { scenario.Name, steps = scenario.Steps.Count });
        });
        api.MapDelete("/scenarios/{name}", (string name, FoundryScenarioStore scenarios) =>
            scenarios.Remove(name) ? Results.Ok() : Results.NotFound());
    }

    private static FoundryUsageQuery Query(HttpRequest request)
    {
        string? Q(string key) => request.Query[key].ToString() is { Length: > 0 } v ? v : null;
        DateTimeOffset? D(string key) => Q(key) is { } v &&
            DateTimeOffset.TryParse(v, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d : null;
        var to = D("to");
        // A bare date as `to` means "through that day", the way engx passes yyyy-MM-dd.
        if (to is { } t && Q("to")!.Length == 10) to = t.AddDays(1);
        return new FoundryUsageQuery(D("from"), to, Q("account"), Q("deployment"), Q("model"), Q("customer"), Q("project"), Q("caller"),
            Q("subscription"), Q("user"));
    }

    private static string Column(string key) => key switch
    {
        "customer" => "customerId",
        "subscription" => "subscriptionId",
        "user" => "userPrincipal",
        "initiative" => "initiativeId",
        _ => key,
    };

    private static string? Key(FoundryUsageRecord r, string key) => key switch
    {
        "day" => r.Timestamp.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        "hour" => r.Timestamp.UtcDateTime.ToString("yyyy-MM-ddTHH:00:00Z", CultureInfo.InvariantCulture),
        "account" => r.Account,
        "deployment" => r.Deployment,
        "model" => r.Model,
        "customer" => r.Customer,
        "project" => r.Project,
        "caller" => r.Caller,
        "subscription" => r.SubscriptionId,
        "user" => r.UserPrincipal,
        "initiative" => r.Initiative,
        "line" => r.Line,
        "agent" => r.Agent,
        "scenario" => r.Scenario,
        "operation" => r.Operation,
        _ => null,
    };

    private sealed class CreateKeyRequest
    {
        public string? Name { get; set; }
        public string? Customer { get; set; }
        public string? Project { get; set; }
        public string? SubscriptionId { get; set; }
        public string? UserPrincipal { get; set; }
        public string? Initiative { get; set; }
        public string? Key { get; set; }

        /// <summary>What a coding agent on this key is told about its project: <c>label</c>, the name
        /// <see cref="FoundryScenarioCall.AskProject"/> offers, and <c>gitRemote</c>.</summary>
        public Dictionary<string, string>? Context { get; set; }
    }
}
