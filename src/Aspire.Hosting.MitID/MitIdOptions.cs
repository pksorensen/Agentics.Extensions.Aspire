namespace Aspire.Hosting;

/// <summary>
/// How an AppHost reaches the MitID test-user service.
/// </summary>
public sealed class MitIdOptions
{
    /// <summary>
    /// The agent-mitid origin, e.g. <c>https://mitid.agentics.dk</c>. Leave null
    /// to read <c>MITID_SERVICE_URL</c> from the environment.
    /// </summary>
    public string? ServiceUrl { get; set; }

    /// <summary>
    /// The MCP bearer token, from the service PWA's Indstillinger tab. Leave null
    /// to read <c>MITID_MCP_TOKEN</c> from the environment or user secrets.
    /// </summary>
    public string? Token { get; set; }

    /// <summary>
    /// The default project key referencing projects should use when they do not
    /// name one. Optional; when set it is injected as <c>MITID_PROJECT</c>.
    /// </summary>
    public string? Project { get; set; }

    /// <summary>
    /// When true (the default), an unconfigured service is not an error: nothing
    /// is injected and tests fall back to driving pp.mitid.dk directly. Set false
    /// to fail AppHost startup instead, for a CI run that must use the registry.
    /// </summary>
    public bool Optional { get; set; } = true;
}
