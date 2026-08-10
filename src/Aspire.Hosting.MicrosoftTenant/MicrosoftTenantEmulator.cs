using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Aspire.Hosting.MicrosoftTenant.Emulator;

public sealed class MicrosoftTenantState
{
    private readonly ConcurrentDictionary<string, MicrosoftTenantAppRegistration> _applications =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<string, MicrosoftTenantUser> _users =
        new(StringComparer.OrdinalIgnoreCase);

    public MicrosoftTenantState(MicrosoftTenantSeed seed, IConfiguration? configuration = null)
    {
        PrimaryDomain = seed.PrimaryDomain;
        TenantId = seed.TenantId;
        PublicUrl = configuration?["MICROSOFT_TENANT_PUBLIC_URL"]?.TrimEnd('/');
        foreach (var app in seed.Applications) _applications[app.ClientId] = app;
        foreach (var user in seed.Users ?? []) _users[user.ObjectId] = user;
    }

    public string PrimaryDomain { get; }
    public string TenantId { get; }

    /// <summary>The address clients reach this emulator on, when the AppHost was
    /// able to say. Null means "believe the request", which is right for a
    /// directly published port and wrong behind a rewriting proxy.</summary>
    public string? PublicUrl { get; }

    public IReadOnlyCollection<MicrosoftTenantAppRegistration> Applications => _applications.Values.ToArray();
    public IReadOnlyCollection<MicrosoftTenantUser> Users => _users.Values.ToArray();

    internal TenantSigningKey SigningKey { get; } = new();
    internal AuthorizationCodeStore Codes { get; } = new();

    /// <summary>Access token → object id, so the userinfo endpoint can answer.
    /// Bounded only by process lifetime, which is fine for a local emulator and
    /// would not be for anything else.</summary>
    internal ConcurrentDictionary<string, string> Sessions { get; } = new(StringComparer.Ordinal);

    public bool ValidateClient(string clientId, string secret) =>
        _applications.TryGetValue(clientId, out var app) &&
        CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(app.ClientSecret),
            Encoding.UTF8.GetBytes(secret));

    public bool KnowsClient(string clientId) => _applications.ContainsKey(clientId);

    internal bool IsThisTenant(string tenantId) =>
        string.Equals(tenantId, TenantId, StringComparison.OrdinalIgnoreCase);

    /// <summary>The base address of this emulator as the caller sees it.</summary>
    internal string BaseUrl(HttpRequest request) =>
        PublicUrl ?? $"{request.Scheme}://{request.Host}{request.PathBase}".TrimEnd('/');

    internal string Authority(HttpRequest request) => $"{BaseUrl(request)}/{TenantId}";

    /// <summary>Entra's v2 issuer, and the value an application must be
    /// configured with. No trailing slash: clients that build the discovery
    /// address by concatenation would otherwise ask for a doubled path.</summary>
    internal string Issuer(HttpRequest request) => $"{Authority(request)}/v2.0";

    public MicrosoftTenantUser? FindUser(string objectId) =>
        _users.TryGetValue(objectId, out var user) ? user : null;

    /// <summary>Every role name any seeded person holds — what the sign-in page
    /// offers when somebody signs in as an address the directory has never seen.</summary>
    internal IReadOnlyList<string> KnownRoles =>
        _users.Values.SelectMany(u => u.Roles).Distinct(StringComparer.Ordinal)
              .OrderBy(r => r, StringComparer.Ordinal).ToArray();

    /// <summary>
    /// Turn what the sign-in page posted into the person signing in — either one
    /// already in the directory, or a new one created on the spot.
    ///
    /// A new person's object id is derived from their address rather than random,
    /// so signing in twice as the same address is signing in twice as the same
    /// identity. An application that binds a pre-provisioned record to a subject
    /// on first sign-in depends on exactly that, and a random id would let it
    /// pass a test it would fail in production.
    /// </summary>
    internal MicrosoftTenantUser? ResolveUser(string objectId, string email, string displayName, IReadOnlyList<string> roles)
    {
        if (!string.IsNullOrWhiteSpace(objectId) && _users.TryGetValue(objectId, out var seeded)) return seeded;
        if (string.IsNullOrWhiteSpace(email) || !email.Contains('@')) return null;

        email = email.Trim();
        var existing = _users.Values.FirstOrDefault(u =>
            string.Equals(u.Email, email, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) return existing;

        var created = new MicrosoftTenantUser(
            ObjectIdFor(email),
            string.IsNullOrWhiteSpace(displayName) ? email.Split('@')[0] : displayName.Trim(),
            email,
            roles);
        _users[created.ObjectId] = created;
        return created;
    }

    public MicrosoftTenantUser AddUser(string email, string? displayName = null, params string[] roles)
    {
        var user = new MicrosoftTenantUser(
            ObjectIdFor(email),
            string.IsNullOrWhiteSpace(displayName) ? email.Split('@')[0] : displayName!,
            email,
            roles);
        _users[user.ObjectId] = user;
        return user;
    }

    /// <summary>A stable GUID for an address — the emulator's stand-in for a
    /// directory object id.</summary>
    public static string ObjectIdFor(string email) =>
        new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(email.Trim().ToLowerInvariant())).AsSpan(0, 16)).ToString();
}

public static class MicrosoftTenantEmulatorExtensions
{
    public static IServiceCollection AddMicrosoftTenantEmulator(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var json = configuration["MICROSOFT_TENANT_SEED_JSON"];
        var seed = string.IsNullOrWhiteSpace(json)
            ? new MicrosoftTenantSeed(
                configuration["MICROSOFT_TENANT_DOMAIN"] ?? "example.test",
                configuration["MICROSOFT_TENANT_ID"] ?? Guid.Empty.ToString(),
                [])
            : JsonSerializer.Deserialize<MicrosoftTenantSeed>(json)
                ?? throw new InvalidOperationException("MICROSOFT_TENANT_SEED_JSON is invalid.");

        services.AddSingleton(seed);
        services.AddSingleton(sp => new MicrosoftTenantState(
            sp.GetRequiredService<MicrosoftTenantSeed>(),
            sp.GetRequiredService<IConfiguration>()));
        return services;
    }

    public static IEndpointRouteBuilder MapMicrosoftTenantEmulator(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/healthz", (MicrosoftTenantState tenant) => Results.Ok(new
        {
            status = "ok",
            tenantId = tenant.TenantId,
            primaryDomain = tenant.PrimaryDomain,
        }));

        endpoints.MapPost("/login/{tenantId}/oauth2/v2.0/token", IssueToken);
        endpoints.MapPost("/{tenantId}/oauth2/v2.0/token", IssueToken);
        endpoints.MapOidc();
        return endpoints;
    }

    private static async Task<IResult> IssueToken(
        string tenantId,
        HttpRequest request,
        MicrosoftTenantState tenant)
    {
        if (!tenant.IsThisTenant(tenantId))
            return OAuthError("invalid_request", $"AADSTS90002: tenant '{tenantId}' not found");

        var form = await request.ReadFormAsync();
        var (clientId, clientSecret) = ClientCredentials(request, form);

        if (string.IsNullOrWhiteSpace(clientId) || !tenant.ValidateClient(clientId, clientSecret))
            return OAuthError("invalid_client", "AADSTS7000215: invalid client secret");

        return form["grant_type"].ToString() switch
        {
            "client_credentials" => Results.Ok(new
            {
                token_type = "Bearer",
                expires_in = 3600,
                access_token = $"tenant:{tenant.TenantId}:app:{clientId}:scope:{form["scope"]}",
            }),
            "authorization_code" => MicrosoftTenantOidc.ExchangeCode(request, form, tenant, clientId),
            var other => OAuthError("unsupported_grant_type", $"AADSTS70000: grant type '{other}' is not emulated"),
        };
    }

    /// <summary>
    /// Client id and secret, from wherever the client library put them. OIDC
    /// libraries default to `client_secret_basic` and put them in the
    /// Authorization header; plenty of others post them in the form. Supporting
    /// only one is how an emulator ends up rejecting a perfectly correct client.
    /// </summary>
    private static (string ClientId, string ClientSecret) ClientCredentials(HttpRequest request, IFormCollection form)
    {
        var header = request.Headers.Authorization.ToString();
        if (header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header[6..].Trim()));
                var split = decoded.IndexOf(':');
                if (split > 0)
                    return (Uri.UnescapeDataString(decoded[..split]), Uri.UnescapeDataString(decoded[(split + 1)..]));
            }
            catch (FormatException) { /* fall through to the form */ }
        }

        return (form["client_id"].ToString(), form["client_secret"].ToString());
    }

    private static IResult OAuthError(string error, string description) =>
        Results.Json(new { error, error_description = description }, statusCode: 401);
}
