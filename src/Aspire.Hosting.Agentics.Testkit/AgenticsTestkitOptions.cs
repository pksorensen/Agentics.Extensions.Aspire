namespace Aspire.Hosting.Agentics.Testkit;

/// <summary>Configuration for the local-only Agentics integration testkit.</summary>
public sealed class AgenticsTestkitOptions
{
    public string Image { get; set; } = "registry.agentics.dk/agentics/agentics-testkit";
    public string Tag { get; set; } = "latest";
    public string Owner { get; set; } = "integrator";
    public string AppId { get; set; } = "testkit-app";
    public string AppName { get; set; } = "Integrator test app";
    public string ClientId { get; set; } = "agentics-testkit-client";
    public string ClientSecret { get; set; } = "local-development-only";
    public string Scopes { get; set; } = "owners:create";
    public bool PersistData { get; set; }
    public string? DataVolumeName { get; set; }
}
