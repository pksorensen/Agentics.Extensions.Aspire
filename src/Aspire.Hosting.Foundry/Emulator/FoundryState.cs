using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace Aspire.Hosting.Foundry.Emulator;

/// <summary>Accounts, deployments and keys — the mutable half of the seed, since
/// deployments and keys can also be created at runtime through ARM or the
/// emulator's own API.</summary>
public sealed class FoundryState
{
    private readonly ConcurrentDictionary<string, FoundryAccount> _accounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, FoundryCallerSeed> _callers = new(StringComparer.OrdinalIgnoreCase);

    public FoundryState(FoundrySeed seed, IConfiguration configuration)
    {
        Behaviour = seed.Behaviour ?? new FoundryBehaviour();
        PublicUrl = configuration["FOUNDRY_PUBLIC_URL"]?.TrimEnd('/');
        foreach (var account in seed.Accounts)
        {
            var state = new FoundryAccount(account.Name, account.SubscriptionId, account.ResourceGroup, account.Location, account.Kind,
                account.Tags ?? new Dictionary<string, string>(), account.Projects ?? [], account.Aliases ?? []);
            foreach (var d in account.Deployments) state.Deployments[d.Name] = d;
            foreach (var k in account.Keys) state.Keys[k.Value] = k;
            // Real accounts always have key1 and key2. Fill whichever slot the seed left
            // empty with a value derived from the name, so it is stable across runs.
            foreach (var slot in new[] { "key1", "key2" })
                if (!state.Keys.Values.Any(k => k.Name == slot))
                    state.Keys[DeriveKey(account.Name, slot)] = new FoundryApiKeySeed(slot, DeriveKey(account.Name, slot));
            _accounts[account.Name] = state;
        }
        foreach (var caller in seed.Callers ?? []) _callers[caller.CallerId] = caller;
        foreach (var key in seed.GatewayKeys ?? []) GatewayKeys[key.Value] = key;
    }

    /// <summary>Emulator-wide keys, the stand-in for gateway (APIM) subscriptions:
    /// valid on every account and every line, each bound to one customer and person.</summary>
    public ConcurrentDictionary<string, FoundryApiKeySeed> GatewayKeys { get; } = new(StringComparer.Ordinal);

    public FoundryBehaviour Behaviour { get; }
    public string? PublicUrl { get; }
    public IReadOnlyCollection<FoundryAccount> Accounts => _accounts.Values.OrderBy(a => a.Name, StringComparer.Ordinal).ToArray();

    /// <summary>By account name, or by an alias such as a gateway business line.</summary>
    public FoundryAccount? FindAccount(string name) =>
        _accounts.TryGetValue(name, out var a) ? a
        : _accounts.Values.FirstOrDefault(x => x.Aliases.Contains(name, StringComparer.OrdinalIgnoreCase));

    /// <summary>The account a host-less v1 route belongs to: the only one, or the
    /// first that has the deployment.</summary>
    public FoundryAccount? DefaultAccountFor(string? deployment) =>
        (deployment is null ? null : Accounts.FirstOrDefault(a => a.FindDeployment(deployment) is not null)) ?? Accounts.FirstOrDefault();

    public FoundryCallerSeed? FindCaller(string id) => _callers.TryGetValue(id, out var c) ? c : null;
    public void PutCaller(FoundryCallerSeed caller) => _callers[caller.CallerId] = caller;
    public IReadOnlyCollection<FoundryCallerSeed> Callers => _callers.Values.ToArray();

    public string BaseUrl(HttpRequest request) =>
        PublicUrl ?? $"{request.Scheme}://{request.Host}{request.PathBase}".TrimEnd('/');

    public static string DeriveKey(string account, string slot) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"foundry-emulator|{account}|{slot}")))[..32].ToLowerInvariant();
}

public sealed class FoundryAccount(
    string name, string subscriptionId, string resourceGroup, string location, string kind,
    IReadOnlyDictionary<string, string> tags, IReadOnlyList<string> projects, IReadOnlyList<string> aliases)
{
    public IReadOnlyList<string> Aliases { get; } = aliases;

    /// <summary>Exact deployment name first, then any deployment that lists it as an alias.</summary>
    public FoundryDeploymentSeed? FindDeployment(string name) =>
        Deployments.TryGetValue(name, out var d) ? d
        : Deployments.Values.FirstOrDefault(x => x.Aliases?.Contains(name, StringComparer.OrdinalIgnoreCase) == true);

    public string Name { get; } = name;
    public string SubscriptionId { get; } = subscriptionId;
    public string ResourceGroup { get; } = resourceGroup;
    public string Location { get; } = location;
    public string Kind { get; } = kind;
    public IReadOnlyDictionary<string, string> Tags { get; } = tags;
    public IReadOnlyList<string> Projects { get; } = projects;
    public ConcurrentDictionary<string, FoundryDeploymentSeed> Deployments { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Key value → key.</summary>
    public ConcurrentDictionary<string, FoundryApiKeySeed> Keys { get; } = new(StringComparer.Ordinal);

    public string ResourceId =>
        $"/subscriptions/{SubscriptionId}/resourceGroups/{ResourceGroup}/providers/Microsoft.CognitiveServices/accounts/{Name}";
}

/// <summary>Who is calling, and on whose behalf.</summary>
public sealed record FoundryCaller(
    string Id,
    string Type,
    string? Customer,
    string? Project,
    string? SimDirective,
    string? SubscriptionId = null,
    string? UserPrincipal = null,
    string? Initiative = null);

/// <summary>
/// Authentication, the way the data plane does it: an <c>api-key</c> header, or an
/// <c>Authorization: Bearer</c> that is either an account key (the OpenAI SDKs and
/// Codex send keys this way on the v1 routes) or an Entra token. Entra tokens are
/// checked against the Microsoft tenant emulator's <c>tokeninfo</c> endpoint,
/// which knows both its opaque client-credential tokens and its signed user tokens.
/// </summary>
public sealed class FoundryAuthenticator(FoundryState state, IHttpClientFactory httpClients)
{
    private readonly ConcurrentDictionary<string, (FoundryCaller? Caller, DateTimeOffset Expires)> _cache = new(StringComparer.Ordinal);

    public async Task<(FoundryCaller? Caller, string? Error)> AuthenticateAsync(HttpRequest request, FoundryAccount account, CancellationToken ct)
    {
        // api-key is Azure's header; Ocp-Apim-Subscription-Key is what a client sends
        // through an APIM gateway in front of Foundry. Both are just keys here.
        var apiKey = request.Headers["api-key"].ToString();
        if (apiKey.Length == 0) apiKey = request.Headers["Ocp-Apim-Subscription-Key"].ToString();
        var bearer = "";
        if (AuthenticationHeaderValue.TryParse(request.Headers.Authorization.ToString(), out var header) &&
            string.Equals(header.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase))
            bearer = header.Parameter ?? "";
        var presented = apiKey.Length > 0 ? apiKey : bearer;

        if (presented.StartsWith("sim-", StringComparison.Ordinal))
        {
            if (!state.Behaviour.AllowSimKeys) return (null, "Simulation keys are disabled on this emulator.");
            var directive = presented[4..];
            return (new FoundryCaller("sim", "sim", null, null, directive.Length == 0 ? null : directive), null);
        }

        if (presented.Length > 0 && (account.Keys.TryGetValue(presented, out var key) || state.GatewayKeys.TryGetValue(presented, out key)))
            return (new FoundryCaller(key.SubscriptionId ?? $"key:{key.Name}", key.SubscriptionId is null ? "apiKey" : "subscription",
                key.Customer, key.Project, null, key.SubscriptionId, key.UserPrincipal, key.Initiative), null);

        if (bearer.Length > 0 && apiKey.Length == 0 && state.Behaviour.TokenInfoUrl is { Length: > 0 } tokenInfo)
        {
            if (_cache.TryGetValue(bearer, out var cached) && cached.Expires > DateTimeOffset.UtcNow)
                return cached.Caller is null ? (null, "The bearer token is not active.") : (cached.Caller, null);

            var caller = await IntrospectAsync(tokenInfo, bearer, ct);
            // Remember a rejection only briefly: the tenant may simply not be up yet.
            _cache[bearer] = (caller, DateTimeOffset.UtcNow.Add(caller is null ? TimeSpan.FromSeconds(10) : TimeSpan.FromMinutes(5)));
            return caller is null ? (null, "The bearer token is not active.") : (caller, null);
        }

        if (!state.Behaviour.RequireAuth)
            return (new FoundryCaller("anonymous", "anonymous", null, null, null), null);

        return (null, presented.Length == 0
            ? "Access denied due to missing subscription key or Entra token. Make sure to include an 'api-key' or 'Authorization' header."
            : "Access denied due to invalid subscription key or wrong API endpoint.");
    }

    private async Task<FoundryCaller?> IntrospectAsync(string url, string token, CancellationToken ct)
    {
        using var message = new HttpRequestMessage(HttpMethod.Get, url);
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        try
        {
            using var response = await httpClients.CreateClient("foundry-tokeninfo").SendAsync(message, ct);
            if (!response.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;
            if (!root.TryGetProperty("active", out var active) || !active.GetBoolean()) return null;
            var type = root.TryGetProperty("principal_type", out var t) ? t.GetString() ?? "user" : "user";
            var id = type == "application"
                ? root.TryGetProperty("appid", out var appid) ? appid.GetString() : null
                : root.TryGetProperty("sub", out var sub) ? sub.GetString() : null;
            if (string.IsNullOrEmpty(id)) return null;
            var mapped = state.FindCaller(id);
            return new FoundryCaller(id, type == "application" ? "servicePrincipal" : "user", mapped?.Customer, mapped?.Project, null);
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }
}
