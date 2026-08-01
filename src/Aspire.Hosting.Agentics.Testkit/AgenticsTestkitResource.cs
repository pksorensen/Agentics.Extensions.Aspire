using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Agentics.Testkit;

/// <summary>A disposable Agentics integration environment hosted in one container.</summary>
public sealed class AgenticsTestkitResource : ContainerResource
{
    internal AgenticsTestkitResource(string name, string owner, string clientId, string clientSecret)
        : base(name)
    {
        Owner = owner;
        ClientId = clientId;
        ClientSecret = clientSecret;
    }

    public const string ApiEndpointName = "http";
    public const string KeycloakEndpointName = "keycloak";
    public const string GitEndpointName = "git";

    public string Owner { get; }
    public string ClientId { get; }
    public string ClientSecret { get; }
}
