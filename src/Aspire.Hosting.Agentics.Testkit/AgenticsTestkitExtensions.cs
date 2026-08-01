using Aspire.Hosting.Agentics.Testkit;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting;

/// <summary>Hosting extensions for the Agentics integration testkit.</summary>
public static class AgenticsTestkitExtensions
{
    public static IResourceBuilder<AgenticsTestkitResource> AddAgenticsTestkit(
        this IDistributedApplicationBuilder builder,
        string name,
        Action<AgenticsTestkitOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var options = new AgenticsTestkitOptions();
        configure?.Invoke(options);
        Validate(options);

        var resource = new AgenticsTestkitResource(name, options.Owner, options.ClientId, options.ClientSecret);
        var testkit = builder.AddResource(resource)
            .WithImage(options.Image, options.Tag)
            .WithHttpEndpoint(targetPort: 3000, name: AgenticsTestkitResource.ApiEndpointName)
            .WithHttpEndpoint(targetPort: 8180, name: AgenticsTestkitResource.KeycloakEndpointName)
            .WithHttpEndpoint(targetPort: 8080, name: AgenticsTestkitResource.GitEndpointName)
            .WithUrlForEndpoint(
                AgenticsTestkitResource.ApiEndpointName,
                url => url.DisplayText = "Agentics API")
            .WithUrlForEndpoint(
                AgenticsTestkitResource.KeycloakEndpointName,
                url => url.DisplayText = "Keycloak")
            .WithUrlForEndpoint(
                AgenticsTestkitResource.GitEndpointName,
                url => url.DisplayText = "Hosted Git")
            .WithEnvironment("AGENTICS_TESTKIT_OWNER", options.Owner)
            .WithEnvironment("AGENTICS_TESTKIT_APP_ID", options.AppId)
            .WithEnvironment("AGENTICS_TESTKIT_APP_NAME", options.AppName)
            .WithEnvironment("AGENTICS_TESTKIT_CLIENT_ID", options.ClientId)
            .WithEnvironment("AGENTICS_TESTKIT_CLIENT_SECRET", options.ClientSecret)
            .WithEnvironment("AGENTICS_TESTKIT_SCOPES", options.Scopes)
            .WithHttpHealthCheck("/api/healthz", endpointName: AgenticsTestkitResource.ApiEndpointName);

        var apiEndpoint = testkit.GetEndpoint(AgenticsTestkitResource.ApiEndpointName);
        var keycloakEndpoint = testkit.GetEndpoint(AgenticsTestkitResource.KeycloakEndpointName);
        var gitEndpoint = testkit.GetEndpoint(AgenticsTestkitResource.GitEndpointName);
        testkit.WithEnvironment(context =>
        {
            // A normal EndpointReference environment value also adds a resource
            // dependency. On the resource's own endpoint that is a self-cycle,
            // so resolve the already-allocated URLs inside the deferred callback.
            context.EnvironmentVariables["AGENTICS_TESTKIT_BASE_URL"] = apiEndpoint.Url;
            context.EnvironmentVariables["AGENTICS_TESTKIT_KEYCLOAK_PUBLIC_URL"] = keycloakEndpoint.Url;
            context.EnvironmentVariables["AGENTICS_TESTKIT_GIT_PUBLIC_URL"] = gitEndpoint.Url;
        });

        if (options.PersistData)
        {
            testkit.WithVolume(options.DataVolumeName ?? $"{name}-data", "/testkit-data");
        }

        return testkit;
    }

    public static IResourceBuilder<T> WithAgenticsTestkit<T>(
        this IResourceBuilder<T> destination,
        IResourceBuilder<AgenticsTestkitResource> testkit)
        where T : IResourceWithEnvironment, IResourceWithWaitSupport
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(testkit);

        var tokenUrl = ReferenceExpression.Create(
            $"{testkit.GetEndpoint(AgenticsTestkitResource.KeycloakEndpointName)}/realms/agentics/protocol/openid-connect/token");

        return destination
            .WithEnvironment("AGENTICS_BASE_URL", testkit.GetEndpoint(AgenticsTestkitResource.ApiEndpointName))
            .WithEnvironment("AGENTICS_TOKEN_URL", tokenUrl)
            .WithEnvironment("AGENTICS_GIT_URL", testkit.GetEndpoint(AgenticsTestkitResource.GitEndpointName))
            .WithEnvironment("AGENTICS_CLIENT_ID", testkit.Resource.ClientId)
            .WithEnvironment("AGENTICS_CLIENT_SECRET", testkit.Resource.ClientSecret)
            .WithEnvironment("AGENTICS_SPONSOR_OWNER", testkit.Resource.Owner)
            .WaitFor(testkit);
    }

    private static void Validate(AgenticsTestkitOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Image);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Tag);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.AppId);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.AppName);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ClientId);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ClientSecret);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Scopes);
    }
}
