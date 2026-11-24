using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using System.Security.Cryptography;
using System.Text;

namespace Aspire.Hosting.MicrosoftTenant.Emulator;

/// <summary>
/// The directory's workload identities: APIs that expose app roles, the managed
/// identities that call them, and who holds which role. Read-only after seeding,
/// like a tenant whose app registrations nobody is editing.
/// </summary>
internal sealed class WorkloadIdentities
{
    private readonly Dictionary<string, MicrosoftTenantApiRegistration> _apisByClientId =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, MicrosoftTenantApiRegistration> _apisByAppIdUri =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, MicrosoftTenantManagedIdentity> _identitiesByHeader =
        new(StringComparer.Ordinal);
    private readonly IReadOnlyList<MicrosoftTenantAppRoleAssignment> _assignments;

    public WorkloadIdentities(MicrosoftTenantSeed seed)
    {
        foreach (var api in seed.Apis ?? [])
        {
            _apisByClientId[api.ClientId] = api;
            _apisByAppIdUri[api.AppIdUri.TrimEnd('/')] = api;
        }
        foreach (var identity in seed.ManagedIdentities ?? [])
            _identitiesByHeader[identity.IdentityHeader] = identity;
        _assignments = seed.AppRoleAssignments ?? [];
    }

    /// <summary>
    /// The API a <c>resource</c> (managed-identity protocol) or the resource part of
    /// a <c>.default</c> scope names. Entra accepts the application ID URI and the
    /// bare client id alike.
    /// </summary>
    public MicrosoftTenantApiRegistration? FindApi(string resource)
    {
        var key = resource.Trim().TrimEnd('/');
        return _apisByAppIdUri.TryGetValue(key, out var api) || _apisByClientId.TryGetValue(key, out api)
            ? api
            : null;
    }

    /// <summary>Which managed identity a request's <c>X-IDENTITY-HEADER</c> belongs to.
    /// Compared in constant time; the header is the identity's secret.</summary>
    public MicrosoftTenantManagedIdentity? FindIdentityByHeader(string header)
    {
        if (string.IsNullOrEmpty(header)) return null;
        foreach (var (candidate, identity) in _identitiesByHeader)
        {
            if (CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(candidate), Encoding.UTF8.GetBytes(header)))
                return identity;
        }
        return null;
    }

    public IReadOnlyList<string> RolesOf(string principalClientId, MicrosoftTenantApiRegistration api) =>
        _assignments
            .Where(a => string.Equals(a.PrincipalClientId, principalClientId, StringComparison.OrdinalIgnoreCase)
                     && string.Equals(a.ApiClientId, api.ClientId, StringComparison.OrdinalIgnoreCase)
                     && api.AppRoles.Contains(a.Role, StringComparer.Ordinal))
            .Select(a => a.Role)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
}

internal static class WorkloadIdentityEndpoints
{
    private static readonly TimeSpan TokenLifetime = TimeSpan.FromHours(1);

    /// <summary>
    /// The App Service managed-identity endpoint (api-version 2019-08-01), which
    /// <c>ManagedIdentityCredential</c> uses when <c>IDENTITY_ENDPOINT</c> and
    /// <c>IDENTITY_HEADER</c> are set. The header says which identity is asking;
    /// <c>client_id</c>, when sent, must name the same one.
    /// </summary>
    public static IEndpointRouteBuilder MapManagedIdentityEndpoint(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/msi/token", (HttpRequest request, MicrosoftTenantState tenant) =>
        {
            var identity = tenant.Workloads.FindIdentityByHeader(request.Headers["X-IDENTITY-HEADER"].ToString());
            if (identity is null)
                return Error(401, "invalid_request", "The X-IDENTITY-HEADER does not belong to a managed identity in this tenant.");

            var clientId = request.Query["client_id"].ToString();
            if (clientId.Length > 0 && !string.Equals(clientId, identity.ClientId, StringComparison.OrdinalIgnoreCase))
                return Error(400, "invalid_request", $"Identity not found: client_id '{clientId}' is not the identity this header belongs to.");

            var resource = request.Query["resource"].ToString();
            if (resource.Length == 0)
                return Error(400, "invalid_request", "The 'resource' parameter is required.");

            var issued = Issue(request, tenant, identity.ClientId, identity.ObjectId, resource, clientAuth: "2");
            if (issued.Error is { } error) return error;

            // The 2019-08-01 shape: expires_on as unix seconds, in a string.
            return Results.Ok(new
            {
                access_token = issued.Token,
                expires_on = issued.ExpiresOn.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture),
                resource,
                token_type = "Bearer",
                client_id = identity.ClientId,
            });
        });
        return endpoints;
    }

    /// <summary><c>api://x/.default</c> → <c>api://x</c>, and a bare
    /// <c>{guid}/.default</c> → <c>{guid}</c>; anything else → null, which keeps
    /// the older opaque client-credentials token for Graph-style callers.</summary>
    public static string? ResourceOfDefaultScope(string scope)
    {
        var single = scope.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (single.Length != 1 || !single[0].EndsWith("/.default", StringComparison.OrdinalIgnoreCase)) return null;
        var resource = single[0][..^"/.default".Length];
        return resource.StartsWith("api://", StringComparison.OrdinalIgnoreCase) || Guid.TryParse(resource, out _)
            ? resource
            : null;
    }

    /// <summary>
    /// A client-credentials JWT for a confidential app registration asking for an
    /// API's <c>.default</c> scope. The app's object id is derived from its client
    /// id, so it is stable across runs.
    /// </summary>
    public static IResult IssueClientCredentialsJwt(
        HttpRequest request, MicrosoftTenantState tenant, string clientId, string resource)
    {
        if (tenant.Workloads.FindApi(resource) is null)
        {
            // Not one of ours (Graph, say): keep the opaque token those callers expect.
            return Results.Ok(new { token_type = "Bearer", expires_in = 3600, access_token = OpaqueFallback(tenant, clientId) });
        }

        var issued = Issue(request, tenant, clientId, MicrosoftTenantState.ObjectIdFor($"app:{clientId}"), resource, clientAuth: "1");
        if (issued.Error is { } error) return error;
        return Results.Ok(new
        {
            token_type = "Bearer",
            expires_in = (int)TokenLifetime.TotalSeconds,
            ext_expires_in = (int)TokenLifetime.TotalSeconds,
            access_token = issued.Token,
        });
    }

    private static string OpaqueFallback(MicrosoftTenantState tenant, string clientId)
    {
        var token = MicrosoftTenantOidc.Base64Url(RandomNumberGenerator.GetBytes(48));
        tenant.ApplicationSessions[token] = clientId;
        return token;
    }

    private readonly record struct Issued(string? Token, DateTimeOffset ExpiresOn, IResult? Error);

    /// <summary>
    /// The access token Entra issues to a workload for an API that asked for v2
    /// tokens: <c>aud</c> is the API's client id, <c>idtyp</c> is <c>app</c>, and
    /// <c>roles</c> holds the caller's app roles. <c>tid</c> is always present —
    /// an app-only token is useless to a multi-tenant check without it.
    /// </summary>
    private static Issued Issue(
        HttpRequest request, MicrosoftTenantState tenant, string principalClientId, string principalObjectId, string resource,
        string clientAuth)
    {
        var api = tenant.Workloads.FindApi(resource);
        if (api is null)
            return new(null, default, Error(400, "invalid_resource",
                $"AADSTS500011: The resource principal named {resource} was not found in the tenant."));

        var roles = tenant.Workloads.RolesOf(principalClientId, api);
        if (roles.Count == 0 && api.AssignmentRequired)
            return new(null, default, Error(400, "invalid_grant",
                $"AADSTS501051: Application '{principalClientId}' is not assigned to a role for the application '{api.ClientId}' ({api.DisplayName})."));

        var now = DateTimeOffset.UtcNow;
        var expires = now + TokenLifetime;
        var claims = new Dictionary<string, object?>
        {
            ["iss"] = tenant.Issuer(request),
            ["aud"] = api.ClientId,
            ["sub"] = principalObjectId,
            ["oid"] = principalObjectId,
            ["azp"] = principalClientId,
            ["appid"] = principalClientId,
            // How the caller authenticated: 1 = client secret, 2 = certificate or managed identity.
            ["azpacr"] = clientAuth,
            ["idtyp"] = "app",
            ["tid"] = tenant.TenantId,
            ["ver"] = "2.0",
            ["uti"] = MicrosoftTenantOidc.Base64Url(RandomNumberGenerator.GetBytes(16)),
            ["iat"] = now.ToUnixTimeSeconds(),
            ["nbf"] = now.ToUnixTimeSeconds(),
            ["exp"] = expires.ToUnixTimeSeconds(),
        };
        // Entra leaves the claim out, rather than sending it empty, when there are none.
        if (roles.Count > 0) claims["roles"] = roles;

        return new(tenant.SigningKey.Sign(claims), expires, null);
    }

    private static IResult Error(int status, string error, string description) =>
        Results.Json(new { error, error_description = description }, statusCode: status);
}
