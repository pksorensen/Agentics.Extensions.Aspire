namespace Aspire.Hosting;

/// <summary>
/// Configures the pre-start check that verifies this machine can pull the
/// AppHost's private container images.
/// </summary>
public sealed class AgenticsRegistryOptions
{
    /// <summary>
    /// The registry hostname whose images are checked. Images from any other
    /// registry are ignored, including Docker Hub.
    /// </summary>
    public string Registry { get; set; } = DefaultRegistry;

    /// <summary>The default registry: <c>registry.agentics.dk</c>.</summary>
    public const string DefaultRegistry = "registry.agentics.dk";

    /// <summary>
    /// Extra image references to check, for images this AppHost pulls without
    /// declaring them as resources — a build script's base image, for example.
    /// </summary>
    public IList<string> AdditionalImages { get; } = [];

    /// <summary>
    /// How long a single probe may take before it is treated as inconclusive.
    /// A slow or offline network must not produce a confident "you are not
    /// signed in" message, so a timeout is reported as unknown, not as denied.
    /// </summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>
    /// The container CLI used for the probe. Docker resolves credentials
    /// through its own chain — config.json, credential helpers — which is the
    /// whole point: the probe answers "can <em>docker</em> pull this", not
    /// "does some config file look right".
    /// </summary>
    public string ContainerRuntime { get; set; } = "docker";

    /// <summary>
    /// The CLI that signs in, as named in the dashboard message.
    /// </summary>
    public string SignInCommand { get; set; } = "agent-registry login";

    /// <summary>
    /// Set false to keep the check out of the dashboard and leave it in the
    /// AppHost log only.
    /// </summary>
    public bool Notify { get; set; } = true;
}
