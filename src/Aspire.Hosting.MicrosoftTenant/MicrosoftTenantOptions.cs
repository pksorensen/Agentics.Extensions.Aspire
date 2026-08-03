namespace Aspire.Hosting.MicrosoftTenant;

public sealed class MicrosoftTenantOptions
{
    public string Image { get; set; } = "ghcr.io/pksorensen/microsoft-tenant-emulator";
    public string Tag { get; set; } = "latest";
    public string? DockerfileContext { get; set; }
    public string DockerfilePath { get; set; } = "src/MicrosoftTenant.Emulator/Dockerfile";
    public bool PersistData { get; set; }
    public string? DataVolumeName { get; set; }
}

public sealed record MicrosoftTenantAppRegistration(
    string DisplayName,
    string ClientId,
    string ClientSecret);

public sealed record MicrosoftTenantSeed(
    string PrimaryDomain,
    string TenantId,
    IReadOnlyList<MicrosoftTenantAppRegistration> Applications);
