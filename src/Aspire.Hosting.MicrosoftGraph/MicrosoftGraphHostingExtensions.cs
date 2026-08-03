using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.MicrosoftTenant;

namespace Aspire.Hosting;

public static class MicrosoftGraphHostingExtensions
{
    public static IResourceBuilder<MicrosoftTenantResource> WithGraphApi(
        this IResourceBuilder<MicrosoftTenantResource> tenant,
        string version = "v1.0") =>
        tenant.WithMicrosoftGraphApi(version);

    public static IResourceBuilder<MicrosoftTenantResource> WithMicrosoftGraphApi(
        this IResourceBuilder<MicrosoftTenantResource> tenant,
        string version = "v1.0")
    {
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);

        if (version is not ("v1.0" or "beta"))
            throw new ArgumentOutOfRangeException(nameof(version), "Supported Graph versions are v1.0 and beta.");

        tenant.Resource.Features["MICROSOFT_GRAPH_EMULATOR_JSON"] =
            new MicrosoftGraphEmulatorSeed(true, version);
        return tenant;
    }
}

public sealed record MicrosoftGraphEmulatorSeed(bool Enabled, string Version);
