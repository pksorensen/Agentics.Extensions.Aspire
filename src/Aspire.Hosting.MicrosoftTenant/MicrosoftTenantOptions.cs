namespace Aspire.Hosting.MicrosoftTenant;

public sealed class MicrosoftTenantOptions
{
    public string Image { get; set; } = "ghcr.io/pksorensen/microsoft-tenant-emulator";
    public string Tag { get; set; } = "latest";
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
}

public sealed record MicrosoftTenantAppRegistration(
    string DisplayName,
    string ClientId,
    string ClientSecret);

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
    IReadOnlyList<string> Roles);

public sealed record MicrosoftTenantSeed(
    string PrimaryDomain,
    string TenantId,
    IReadOnlyList<MicrosoftTenantAppRegistration> Applications,
    // Optional so the record stays source-compatible with callers written before
    // the tenant had users at all.
    IReadOnlyList<MicrosoftTenantUser>? Users = null);
