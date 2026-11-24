namespace Aspire.Hosting.MicrosoftTenant;

public sealed class MicrosoftTenantOptions
{
    public string Image { get; set; } = "ghcr.io/pksorensen/microsoft-tenant-emulator";
    /// <summary>
    /// The emulator image tag. Defaults to this package's own version, so a host
    /// pulls the image released together with the package it compiled against and
    /// the two cannot drift. Set it to pin something else, such as <c>latest</c>.
    /// </summary>
    public string Tag { get; set; } = PackageVersion;

    /// <summary>The version this assembly was released as, without build metadata.</summary>
    internal static string PackageVersion { get; } =
        (typeof(MicrosoftTenantOptions).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?.InformationalVersion ?? "latest")
        .Split('+')[0];
    public string? DockerfileContext { get; set; }
    public string DockerfilePath { get; set; } = "src/MicrosoftTenant.Emulator/Dockerfile";
    public bool PersistData { get; set; }
    public string? DataVolumeName { get; set; }

    /// <summary>
    /// A fixed host port for the emulator's HTTP endpoint, published without the
    /// Aspire proxy.
    ///
    /// This exists because of the issuer. An OIDC client compares the `issuer` in
    /// the discovery document against the authority it was configured with, byte
    /// for byte, and refuses the sign-in when they differ. The emulator therefore
    /// has to know the address the *client* uses to reach it, which it cannot
    /// learn from a Host header that a reverse proxy has already rewritten. A
    /// port that does not move between runs makes that address a constant the
    /// AppHost can state once — the same reason the application under test pins
    /// its own port for a redirect URI.
    ///
    /// Leave it null and no login flow will work unless <see cref="PublicUrl"/>
    /// is set instead.
    /// </summary>
    public int? HostPort { get; set; }

    /// <summary>
    /// The address clients use to reach the emulator, if it is not
    /// <c>http://localhost:{HostPort}</c>. Setting this overrides
    /// <see cref="HostPort"/> for issuer purposes only — the endpoint still binds
    /// where <see cref="HostPort"/> says.
    /// </summary>
    public string? PublicUrl { get; set; }

    /// <summary>
    /// Whether the sign-in page offers "somebody else" — an address the directory
    /// has never seen. On by default, because "invite somebody, then be them" is a
    /// flow worth testing. Turn it off for a demo whose picker should show the
    /// seeded people and nobody else; the authorize endpoint then refuses unseeded
    /// addresses too, so the list on the page is the whole directory.
    /// </summary>
    public bool AllowUnknownUsers { get; set; } = true;

    /// <summary>
    /// Emit <c>tid</c> on tokens even when the issuer is plain http.
    ///
    /// By default the claim is withheld over http to keep Auth.js from re-running
    /// discovery against an https-only check (see MicrosoftTenantOidc.ExchangeCode).
    /// MSAL.js has no such branch and keys its account cache on the tenant, so an
    /// SPA client wants the claim exactly as Entra emits it.
    /// </summary>
    public bool AlwaysEmitTenantIdClaim { get; set; }
}

/// <summary>
/// An app registration in the emulated tenant.
///
/// A null <paramref name="ClientSecret"/> together with
/// <see cref="AllowPublicClient"/> is a public client — an SPA or a native app —
/// which redeems codes with PKCE and no secret, the way Entra's "Single-page
/// application" platform does.
/// </summary>
public sealed record MicrosoftTenantAppRegistration(
    string DisplayName,
    string ClientId,
    string? ClientSecret)
{
    /// <summary>Whether the token endpoint accepts this client without a secret,
    /// for the authorization-code (PKCE required) and refresh-token grants.</summary>
    public bool AllowPublicClient { get; init; }
}

/// <summary>
/// A person in the emulated directory.
///
/// <paramref name="Roles"/> are app-role names exactly as Entra puts them on the
/// <c>roles</c> claim — opaque strings to the emulator, which never interprets
/// them.
/// </summary>
public sealed record MicrosoftTenantUser(
    string ObjectId,
    string DisplayName,
    string Email,
    IReadOnlyList<string> Roles)
{
    /// <summary>Shown under the name on the sign-in page — for a demo, the part
    /// this person plays. Not a token claim.</summary>
    public string? JobTitle { get; init; }
}

public sealed record MicrosoftTenantSeed(
    string PrimaryDomain,
    string TenantId,
    IReadOnlyList<MicrosoftTenantAppRegistration> Applications,
    // Optional so the record stays source-compatible with callers written before
    // the tenant had users at all.
    IReadOnlyList<MicrosoftTenantUser>? Users = null,
    // Workload identities: APIs that expose app roles, managed identities that
    // call them, and which identity holds which role. Optional for the same reason.
    IReadOnlyList<MicrosoftTenantApiRegistration>? Apis = null,
    IReadOnlyList<MicrosoftTenantManagedIdentity>? ManagedIdentities = null,
    IReadOnlyList<MicrosoftTenantAppRoleAssignment>? AppRoleAssignments = null);

/// <summary>
/// An app registration that represents an API — a resource other workloads ask
/// for tokens to. Tokens for it carry <c>aud</c> = <paramref name="ClientId"/>
/// (Entra's v2 access-token format) and the caller's app roles in <c>roles</c>.
///
/// <paramref name="AssignmentRequired"/> mirrors "Assignment required?" on the
/// enterprise app: a caller holding none of <paramref name="AppRoles"/> is refused
/// a token, rather than given one without roles.
/// </summary>
public sealed record MicrosoftTenantApiRegistration(
    string DisplayName,
    string ClientId,
    string AppIdUri,
    IReadOnlyList<string> AppRoles,
    bool AssignmentRequired = true);

/// <summary>
/// A managed identity, as an Azure resource would have one. A workload uses it
/// through the App Service managed-identity protocol (<c>IDENTITY_ENDPOINT</c> +
/// <c>IDENTITY_HEADER</c>), so <c>ManagedIdentityCredential</c> works unchanged.
/// <paramref name="IdentityHeader"/> is the secret that identifies the caller as
/// this identity.
/// </summary>
public sealed record MicrosoftTenantManagedIdentity(
    string Name,
    string ClientId,
    string ObjectId,
    string IdentityHeader);

/// <summary>
/// An app role assigned to a workload — a managed identity or a confidential app
/// registration, named by its client id — on an API, named by its client id.
/// </summary>
public sealed record MicrosoftTenantAppRoleAssignment(
    string PrincipalClientId,
    string ApiClientId,
    string Role);
