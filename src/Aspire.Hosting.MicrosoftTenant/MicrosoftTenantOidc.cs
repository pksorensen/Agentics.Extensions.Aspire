using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Aspire.Hosting.MicrosoftTenant.Emulator;

/// <summary>
/// The sign-in half of the tenant emulator: enough OpenID Connect for a real
/// OIDC client library to complete an authorization-code flow against it.
///
/// What this deliberately is not: there is no password, no multi-factor prompt,
/// no consent screen and no conditional access. Signing in means picking a name
/// off a list. The point is to exercise *the application's* handling of an
/// identity — the roles claim, the object id, first-sign-in binding — without a
/// directory, a tenant administrator or a client secret that has to stay secret.
/// </summary>
public static class MicrosoftTenantOidc
{
    // Ten minutes is the Entra authorization-code lifetime. Nothing here depends
    // on the number; matching it means a test that sits on a breakpoint fails
    // the same way against both.
    private static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan TokenLifetime = TimeSpan.FromHours(1);

    internal static IEndpointRouteBuilder MapOidc(this IEndpointRouteBuilder endpoints)
    {
        // Auth.js composes the discovery address by *string concatenation* —
        // `${issuer}/.well-known/openid-configuration` — so the document has to
        // sit directly under the issuer path. Entra also answers the tenant-root
        // form, and so do we, because half the OIDC libraries in the world use
        // one and half use the other.
        endpoints.MapGet("/{tenantId}/v2.0/.well-known/openid-configuration", Discovery);
        endpoints.MapGet("/{tenantId}/.well-known/openid-configuration", Discovery);
        endpoints.MapGet("/{tenantId}/discovery/v2.0/keys", Keys);

        endpoints.MapGet("/{tenantId}/oauth2/v2.0/authorize", AuthorizePage);
        endpoints.MapPost("/{tenantId}/oauth2/v2.0/authorize", AuthorizeSubmit);

        endpoints.MapGet("/{tenantId}/oauth2/v2.0/logout", Logout);
        endpoints.MapGet("/{tenantId}/openid/userinfo", UserInfo);
        endpoints.MapPost("/{tenantId}/openid/userinfo", UserInfo);

        return endpoints;
    }

    // ------------------------------------------------------------------
    // Metadata
    // ------------------------------------------------------------------

    private static IResult Discovery(string tenantId, HttpRequest request, MicrosoftTenantState tenant)
    {
        if (!tenant.IsThisTenant(tenantId)) return TenantNotFound(tenantId);

        var authority = tenant.Authority(request);
        return Results.Json(new
        {
            issuer = tenant.Issuer(request),
            authorization_endpoint = $"{authority}/oauth2/v2.0/authorize",
            token_endpoint = $"{authority}/oauth2/v2.0/token",
            jwks_uri = $"{authority}/discovery/v2.0/keys",
            userinfo_endpoint = $"{authority}/openid/userinfo",
            end_session_endpoint = $"{authority}/oauth2/v2.0/logout",
            response_modes_supported = new[] { "query", "fragment", "form_post" },
            response_types_supported = new[] { "code", "id_token", "code id_token" },
            grant_types_supported = new[] { "authorization_code", "client_credentials", "refresh_token" },
            subject_types_supported = new[] { "pairwise" },
            id_token_signing_alg_values_supported = new[] { "RS256" },
            scopes_supported = new[] { "openid", "profile", "email", "offline_access" },
            token_endpoint_auth_methods_supported = new[] { "client_secret_post", "client_secret_basic" },
            code_challenge_methods_supported = new[] { "S256", "plain" },
            claims_supported = new[]
            {
                "sub", "oid", "tid", "iss", "aud", "exp", "iat", "nbf", "nonce",
                "name", "email", "preferred_username", "roles", "ver",
            },
        });
    }

    private static IResult Keys(string tenantId, MicrosoftTenantState tenant) =>
        tenant.IsThisTenant(tenantId)
            ? Results.Json(new { keys = new[] { tenant.SigningKey.PublicJwk() } })
            : TenantNotFound(tenantId);

    // ------------------------------------------------------------------
    // The sign-in page
    // ------------------------------------------------------------------

    private static IResult AuthorizePage(string tenantId, HttpRequest request, MicrosoftTenantState tenant)
    {
        if (!tenant.IsThisTenant(tenantId)) return TenantNotFound(tenantId);

        var clientId = request.Query["client_id"].ToString();
        if (string.IsNullOrWhiteSpace(clientId) || !tenant.KnowsClient(clientId))
            return OAuthError("unauthorized_client", $"AADSTS700016: application '{clientId}' not found in this tenant");

        if (string.IsNullOrWhiteSpace(request.Query["redirect_uri"].ToString()))
            return OAuthError("invalid_request", "AADSTS900971: no redirect_uri");

        return Results.Content(SignInPage(tenant, request), "text/html; charset=utf-8");
    }

    private static async Task<IResult> AuthorizeSubmit(
        string tenantId,
        HttpRequest request,
        MicrosoftTenantState tenant)
    {
        if (!tenant.IsThisTenant(tenantId)) return TenantNotFound(tenantId);

        var query = request.Query;
        var clientId = query["client_id"].ToString();
        var redirectUri = query["redirect_uri"].ToString();
        if (string.IsNullOrWhiteSpace(clientId) || !tenant.KnowsClient(clientId))
            return OAuthError("unauthorized_client", $"AADSTS700016: application '{clientId}' not found in this tenant");
        if (string.IsNullOrWhiteSpace(redirectUri))
            return OAuthError("invalid_request", "AADSTS900971: no redirect_uri");

        var form = await request.ReadFormAsync();

        // Two ways in: a seeded identity picked off the list, or an address typed
        // into the box. The second one is what makes "an administrator invites
        // somebody, and then that somebody signs in" testable — the invited
        // person does not exist in the directory until they arrive.
        var user = tenant.ResolveUser(
            objectId: form["subject"].ToString(),
            email: form["email"].ToString(),
            displayName: form["displayName"].ToString(),
            roles: form["roles"].Select(r => r ?? string.Empty).Where(r => r.Length > 0).ToArray());

        if (user is null)
            return Results.Content(SignInPage(tenant, request, error: "Pick a person, or type an email address."),
                "text/html; charset=utf-8");

        var code = tenant.Codes.Issue(new AuthorizationCode(
            ClientId: clientId,
            RedirectUri: redirectUri,
            Subject: user.ObjectId,
            Nonce: query["nonce"].ToString(),
            CodeChallenge: query["code_challenge"].ToString(),
            CodeChallengeMethod: query["code_challenge_method"].ToString(),
            Scope: query["scope"].ToString(),
            ExpiresAt: DateTimeOffset.UtcNow + CodeLifetime));

        var destination = new UriBuilder(redirectUri);
        var separator = string.IsNullOrEmpty(destination.Query) ? string.Empty : destination.Query.TrimStart('?') + "&";
        var state = query["state"].ToString();
        destination.Query = separator
            + $"code={WebUtility.UrlEncode(code)}"
            + (string.IsNullOrEmpty(state) ? string.Empty : $"&state={WebUtility.UrlEncode(state)}");

        return Results.Redirect(destination.Uri.ToString());
    }

    // ------------------------------------------------------------------
    // Tokens
    // ------------------------------------------------------------------

    internal static IResult ExchangeCode(
        HttpRequest request,
        IFormCollection form,
        MicrosoftTenantState tenant,
        string clientId)
    {
        var code = form["code"].ToString();
        if (!tenant.Codes.TryRedeem(code, out var grant))
            return OAuthError("invalid_grant", "AADSTS70008: the authorization code is expired or already used");

        if (!string.Equals(grant.ClientId, clientId, StringComparison.OrdinalIgnoreCase))
            return OAuthError("invalid_grant", "AADSTS50148: the code was issued to a different client");

        var redirectUri = form["redirect_uri"].ToString();
        if (!string.IsNullOrEmpty(redirectUri) &&
            !string.Equals(redirectUri, grant.RedirectUri, StringComparison.OrdinalIgnoreCase))
            return OAuthError("invalid_grant", "AADSTS50011: redirect_uri does not match the one on the request");

        if (!PkceHolds(grant, form["code_verifier"].ToString()))
            return OAuthError("invalid_grant", "AADSTS501481: the code_verifier does not match the code_challenge");

        var user = tenant.FindUser(grant.Subject);
        if (user is null)
            return OAuthError("invalid_grant", "AADSTS50034: the account no longer exists in this tenant");

        var now = DateTimeOffset.UtcNow;
        var issuer = tenant.Issuer(request);

        // `tid` is withheld over plain http, and only over plain http.
        //
        // Auth.js (`@auth/core` 0.41.3, lib/actions/callback/oauth/callback.js)
        // carries a conformance branch for the `microsoft-entra-id` provider:
        // when the id_token has a string `tid`, it re-runs OIDC discovery against
        // the tenant the token names, to follow a user home from `common`. That
        // second `discoveryRequest` is issued *without* `allowInsecureRequests`,
        // which the first one sets — so oauth4webapi's `checkProtocol` throws
        // "only requests to HTTPS are allowed" and every sign-in dies at the
        // callback. Nothing an application can pass in reaches it: the flag lives
        // behind a private `Symbol("conform-internal")`, and the check runs before
        // any `customFetch`.
        //
        // Over https the claim is emitted exactly as Entra emits it, so a
        // consumer that reads `tid` — or that re-discovers, as Auth.js does —
        // behaves identically to production. The deviation is scoped to the one
        // configuration where the alternative is that nothing works at all, and
        // disappears on its own the day this emulator serves TLS.
        var emitTenantIdClaim = issuer.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

        var accessClaims = new Dictionary<string, object?>
        {
            ["iss"] = issuer,
            ["aud"] = "00000003-0000-0000-c000-000000000000", // Microsoft Graph, as Entra does
            ["sub"] = user.ObjectId,
            ["oid"] = user.ObjectId,
            ["appid"] = clientId,
            ["scp"] = string.IsNullOrEmpty(grant.Scope) ? "openid profile email" : grant.Scope,
            ["preferred_username"] = user.Email,
            ["iat"] = now.ToUnixTimeSeconds(),
            ["nbf"] = now.ToUnixTimeSeconds(),
            ["exp"] = (now + TokenLifetime).ToUnixTimeSeconds(),
        };
        if (emitTenantIdClaim) accessClaims["tid"] = tenant.TenantId;

        var accessToken = tenant.SigningKey.Sign(accessClaims);

        var idClaims = new Dictionary<string, object?>
        {
            ["iss"] = issuer,
            ["aud"] = clientId,
            ["sub"] = user.ObjectId,
            ["oid"] = user.ObjectId,
            ["ver"] = "2.0",
            ["name"] = user.DisplayName,
            ["email"] = user.Email,
            ["preferred_username"] = user.Email,
            ["iat"] = now.ToUnixTimeSeconds(),
            ["nbf"] = now.ToUnixTimeSeconds(),
            ["exp"] = (now + TokenLifetime).ToUnixTimeSeconds(),
        };
        // Entra omits `roles` entirely when the account holds none. Emitting an
        // empty array instead would let an application that reads `roles?.length`
        // pass here and fail against the real directory.
        if (user.Roles.Count > 0) idClaims["roles"] = user.Roles;
        if (emitTenantIdClaim) idClaims["tid"] = tenant.TenantId;
        if (!string.IsNullOrEmpty(grant.Nonce)) idClaims["nonce"] = grant.Nonce;

        tenant.Sessions[accessToken] = user.ObjectId;

        return Results.Ok(new
        {
            token_type = "Bearer",
            expires_in = (int)TokenLifetime.TotalSeconds,
            scope = string.IsNullOrEmpty(grant.Scope) ? "openid profile email" : grant.Scope,
            access_token = accessToken,
            id_token = tenant.SigningKey.Sign(idClaims),
        });
    }

    private static bool PkceHolds(AuthorizationCode grant, string verifier)
    {
        if (string.IsNullOrEmpty(grant.CodeChallenge)) return true;
        if (string.IsNullOrEmpty(verifier)) return false;

        if (string.Equals(grant.CodeChallengeMethod, "plain", StringComparison.OrdinalIgnoreCase))
            return CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(verifier), Encoding.UTF8.GetBytes(grant.CodeChallenge));

        var hashed = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(hashed), Encoding.UTF8.GetBytes(grant.CodeChallenge));
    }

    private static IResult UserInfo(string tenantId, HttpRequest request, MicrosoftTenantState tenant)
    {
        if (!tenant.IsThisTenant(tenantId)) return TenantNotFound(tenantId);

        var header = request.Headers.Authorization.ToString();
        var token = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header[7..].Trim() : string.Empty;
        if (token.Length == 0 || !tenant.Sessions.TryGetValue(token, out var subject))
            return Results.Json(new { error = "invalid_token" }, statusCode: 401);

        var user = tenant.FindUser(subject);
        if (user is null) return Results.Json(new { error = "invalid_token" }, statusCode: 401);

        return Results.Json(new
        {
            sub = user.ObjectId,
            oid = user.ObjectId,
            name = user.DisplayName,
            email = user.Email,
            preferred_username = user.Email,
        });
    }

    // ------------------------------------------------------------------
    // Sign-out
    // ------------------------------------------------------------------

    private static IResult Logout(string tenantId, HttpRequest request, MicrosoftTenantState tenant)
    {
        if (!tenant.IsThisTenant(tenantId)) return TenantNotFound(tenantId);

        // There is no browser session at the emulator to end — every sign-in
        // shows the picker — so honouring the redirect *is* the whole job.
        // Unlike Entra we do not validate it against a registered reply URL: the
        // application under test would otherwise have to register its post-logout
        // address twice, once for real and once here.
        var destination = request.Query["post_logout_redirect_uri"].ToString();
        return string.IsNullOrWhiteSpace(destination)
            ? Results.Content(Page("Signed out", "<p>You are signed out.</p>"), "text/html; charset=utf-8")
            : Results.Redirect(destination);
    }

    // ------------------------------------------------------------------
    // HTML
    // ------------------------------------------------------------------

    private static string SignInPage(MicrosoftTenantState tenant, HttpRequest request, string? error = null)
    {
        var action = WebUtility.HtmlEncode($"{request.Path}{request.QueryString}");
        var clientId = request.Query["client_id"].ToString();
        var application = tenant.Applications.FirstOrDefault(a =>
            string.Equals(a.ClientId, clientId, StringComparison.OrdinalIgnoreCase));

        var body = new StringBuilder();
        body.Append($"<p class=\"lede\">Sign in to <strong>{WebUtility.HtmlEncode(application?.DisplayName ?? clientId)}</strong>");
        body.Append($" — {WebUtility.HtmlEncode(tenant.PrimaryDomain)}</p>");

        if (error is not null)
            body.Append($"<p class=\"error\" data-testid=\"signin-error\">{WebUtility.HtmlEncode(error)}</p>");

        body.Append("<ul class=\"people\">");
        foreach (var user in tenant.Users.OrderBy(u => u.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            body.Append($"<li><form method=\"post\" action=\"{action}\">");
            body.Append($"<input type=\"hidden\" name=\"subject\" value=\"{WebUtility.HtmlEncode(user.ObjectId)}\">");
            body.Append($"<button type=\"submit\" data-testid=\"sign-in-as\" data-email=\"{WebUtility.HtmlEncode(user.Email)}\">");
            body.Append($"<span class=\"name\">{WebUtility.HtmlEncode(user.DisplayName)}</span>");
            body.Append($"<span class=\"email\">{WebUtility.HtmlEncode(user.Email)}</span>");
            body.Append(user.Roles.Count == 0
                ? "<span class=\"roles none\">no directory roles</span>"
                : $"<span class=\"roles\">{WebUtility.HtmlEncode(string.Join(" · ", user.Roles))}</span>");
            body.Append("</button></form></li>");
        }
        body.Append("</ul>");

        body.Append($"<form method=\"post\" action=\"{action}\" class=\"other\" data-testid=\"sign-in-other\">");
        body.Append("<h2>Somebody else</h2>");
        body.Append("<p>An address that is not in the directory yet signs in as a new person with a stable object id, "
                  + "so returning as the same address returns as the same identity.</p>");
        body.Append("<label>Email <input type=\"email\" name=\"email\" data-testid=\"other-email\" required "
                  + $"placeholder=\"someone@{WebUtility.HtmlEncode(tenant.PrimaryDomain)}\"></label>");
        body.Append("<label>Name <input type=\"text\" name=\"displayName\" data-testid=\"other-name\" placeholder=\"optional\"></label>");

        var roles = tenant.KnownRoles;
        if (roles.Count > 0)
        {
            body.Append("<fieldset><legend>Directory roles</legend>");
            foreach (var role in roles)
                body.Append($"<label class=\"role\"><input type=\"checkbox\" name=\"roles\" value=\"{WebUtility.HtmlEncode(role)}\" "
                          + $"data-testid=\"other-role\" data-role=\"{WebUtility.HtmlEncode(role)}\"> {WebUtility.HtmlEncode(role)}</label>");
            body.Append("</fieldset>");
        }

        body.Append("<button type=\"submit\" data-testid=\"sign-in-other-submit\">Sign in</button>");
        body.Append("</form>");

        return Page("Sign in", body.ToString());
    }

    private static string Page(string title, string body) =>
        $$"""
        <!doctype html>
        <html lang="en"><head><meta charset="utf-8">
        <meta name="viewport" content="width=device-width,initial-scale=1">
        <title>{{WebUtility.HtmlEncode(title)}} — Microsoft tenant emulator</title>
        <style>
          :root { color-scheme: light dark; --line: color-mix(in srgb, currentColor 15%, transparent); }
          body { font: 15px/1.55 system-ui, sans-serif; margin: 0; padding: 3rem 1.25rem; display: flex; justify-content: center; }
          main { width: 100%; max-width: 30rem; }
          h1 { font-size: 1.4rem; margin: 0 0 .25rem; }
          h2 { font-size: 1rem; margin: 0 0 .5rem; }
          .lede, .other p { opacity: .7; margin: 0 0 1.5rem; }
          .error { color: #b3261e; margin: 0 0 1rem; }
          ul.people { list-style: none; margin: 0 0 2rem; padding: 0; display: grid; gap: .5rem; }
          ul.people button { width: 100%; text-align: left; display: grid; gap: .1rem; padding: .8rem 1rem;
            border: 1px solid var(--line); border-radius: .5rem; background: transparent; color: inherit;
            font: inherit; cursor: pointer; }
          ul.people button:hover { border-color: currentColor; }
          .name { font-weight: 600; }
          .email, .roles { font-size: .85em; opacity: .7; }
          .roles.none { font-style: italic; }
          .other { border-top: 1px solid var(--line); padding-top: 1.5rem; display: grid; gap: .75rem; }
          .other label { display: grid; gap: .25rem; font-size: .85em; }
          .other input[type=email], .other input[type=text] { font: inherit; padding: .5rem .6rem;
            border: 1px solid var(--line); border-radius: .4rem; background: transparent; color: inherit; }
          fieldset { border: 1px solid var(--line); border-radius: .4rem; }
          legend { font-size: .8em; opacity: .7; }
          label.role { display: flex; flex-direction: row; align-items: center; gap: .4rem; }
          .other > button { justify-self: start; font: inherit; padding: .55rem 1.2rem; border-radius: .4rem;
            border: 1px solid currentColor; background: transparent; color: inherit; cursor: pointer; }
          footer { margin-top: 2.5rem; font-size: .8em; opacity: .55; }
        </style></head>
        <body><main>
        <h1>{{WebUtility.HtmlEncode(title)}}</h1>
        {{body}}
        <footer>Microsoft tenant emulator — no password, no MFA, no consent. Never reachable outside local development.</footer>
        </main></body></html>
        """;

    // ------------------------------------------------------------------

    internal static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static IResult TenantNotFound(string tenantId) =>
        OAuthError("invalid_request", $"AADSTS90002: tenant '{tenantId}' not found");

    private static IResult OAuthError(string error, string description) =>
        Results.Json(new { error, error_description = description }, statusCode: 400);
}

/// <summary>One issued authorization code, redeemable exactly once.</summary>
internal sealed record AuthorizationCode(
    string ClientId,
    string RedirectUri,
    string Subject,
    string Nonce,
    string CodeChallenge,
    string CodeChallengeMethod,
    string Scope,
    DateTimeOffset ExpiresAt);

internal sealed class AuthorizationCodeStore
{
    private readonly ConcurrentDictionary<string, AuthorizationCode> _codes = new(StringComparer.Ordinal);

    public string Issue(AuthorizationCode code)
    {
        var value = MicrosoftTenantOidc.Base64Url(RandomNumberGenerator.GetBytes(32));
        _codes[value] = code;
        Sweep();
        return value;
    }

    /// <summary>Redemption removes the code — replaying one is the attack the
    /// single-use rule exists for, and a test that accidentally replays should
    /// see the same failure a real client would.</summary>
    public bool TryRedeem(string value, out AuthorizationCode code)
    {
        code = default!;
        if (string.IsNullOrEmpty(value) || !_codes.TryRemove(value, out var found)) return false;
        if (found.ExpiresAt <= DateTimeOffset.UtcNow) return false;
        code = found;
        return true;
    }

    private void Sweep()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var (key, value) in _codes)
            if (value.ExpiresAt <= now) _codes.TryRemove(key, out _);
    }
}

/// <summary>
/// The tenant's token-signing key: RSA, generated on startup and never
/// persisted. A restart rotates it, which invalidates every token the emulator
/// has issued — the same thing a key rollover does, and cheap to reason about
/// because nothing here is meant to outlive the process.
/// </summary>
internal sealed class TenantSigningKey
{
    private readonly RSA _rsa = RSA.Create(2048);

    public string KeyId { get; } = MicrosoftTenantOidc.Base64Url(RandomNumberGenerator.GetBytes(16));

    public object PublicJwk()
    {
        var parameters = _rsa.ExportParameters(includePrivateParameters: false);
        return new
        {
            kty = "RSA",
            use = "sig",
            alg = "RS256",
            kid = KeyId,
            n = MicrosoftTenantOidc.Base64Url(parameters.Modulus!),
            e = MicrosoftTenantOidc.Base64Url(parameters.Exponent!),
        };
    }

    public string Sign(IDictionary<string, object?> claims)
    {
        var header = MicrosoftTenantOidc.Base64Url(JsonSerializer.SerializeToUtf8Bytes(
            new { typ = "JWT", alg = "RS256", kid = KeyId }));
        var payload = MicrosoftTenantOidc.Base64Url(JsonSerializer.SerializeToUtf8Bytes(claims));
        var signingInput = Encoding.ASCII.GetBytes($"{header}.{payload}");
        var signature = MicrosoftTenantOidc.Base64Url(
            _rsa.SignData(signingInput, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        return $"{header}.{payload}.{signature}";
    }
}
