using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.MicrosoftTenant;

public sealed class MicrosoftTenantResource : ContainerResource
{
    internal MicrosoftTenantResource(string name, string primaryDomain, string tenantId)
        : base(name)
    {
        PrimaryDomain = primaryDomain;
        TenantId = tenantId;
    }

    public const string HttpEndpointName = "http";

    public string PrimaryDomain { get; }
    public string TenantId { get; }

    /// <summary>The address clients reach this emulator on, when it is knowable
    /// before startup. Null disables the sign-in flow — see
    /// <see cref="MicrosoftTenantOptions.HostPort"/>.</summary>
    public string? PublicUrl { get; init; }

    public IList<MicrosoftTenantAppRegistration> Applications { get; } = [];
    public IList<MicrosoftTenantUser> Users { get; } = [];

    // Feature packages put their versioned seed documents here. Keeping the
    // tenant package ignorant of Graph and ARM avoids a dependency cycle.
    public IDictionary<string, object> Features { get; } =
        new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
}
