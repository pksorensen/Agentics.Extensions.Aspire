using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Agentics.Testkit;

/// <summary>A disposable Agentics integration environment hosted in one container.</summary>
public sealed class AgenticsTestkitResource : ContainerResource
{
    internal AgenticsTestkitResource(string name, AgenticsTestkitOptions options)
        : base(name)
    {
        Options = options;
    }

    public const string ApiEndpointName = "http";
    public const string KeycloakEndpointName = "keycloak";
    public const string GitEndpointName = "git";
    public const string GitSshEndpointName = "git-ssh";

    public AgenticsTestkitOptions Options { get; }

    public string Owner => Options.Owner;
    public string ClientId => Options.ClientId;
    public string ClientSecret => Options.ClientSecret;

    internal List<AgenticsTestkitUser> Users { get; } = [];
}

internal sealed record AgenticsTestkitUser(
    string Email,
    string Name,
    string Handle,
    string Password,
    bool IsAdmin);
