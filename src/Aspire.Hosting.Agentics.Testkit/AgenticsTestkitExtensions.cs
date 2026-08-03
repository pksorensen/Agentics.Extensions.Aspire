using Aspire.Hosting.Agentics.Testkit;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using System.Text.Json;

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

        var options = new AgenticsTestkitOptions
        {
            Image = builder.Configuration["AGENTICS_TESTKIT_IMAGE"]
                ?? AgenticsTestkitOptions.DefaultImage,
            Tag = builder.Configuration["AGENTICS_TESTKIT_TAG"]
                ?? AgenticsTestkitOptions.DefaultTag,
        };
        configure?.Invoke(options);
        Validate(options);

        var resource = new AgenticsTestkitResource(name, options);
        var testkit = builder.AddResource(resource)
            .WithImage(options.Image, options.Tag)
            // Integration webhooks commonly call an application running as an
            // Aspire host process. Linux containers do not provide Docker
            // Desktop's host alias automatically, so make that callback path
            // portable across local Docker environments.
            .WithContainerRuntimeArgs("--add-host", "host.docker.internal:host-gateway")
            .WithHttpEndpoint(targetPort: 3000, name: AgenticsTestkitResource.ApiEndpointName)
            .WithHttpEndpoint(targetPort: 8180, name: AgenticsTestkitResource.KeycloakEndpointName)
            .WithHttpEndpoint(targetPort: 8080, name: AgenticsTestkitResource.GitEndpointName)
            .WithEndpoint(targetPort: 2222, name: AgenticsTestkitResource.GitSshEndpointName, scheme: "tcp")
            .WithUrlForEndpoint(
                AgenticsTestkitResource.ApiEndpointName,
                url => url.DisplayText = "Agentics API")
            .WithUrlForEndpoint(
                AgenticsTestkitResource.KeycloakEndpointName,
                url => url.DisplayText = "Keycloak")
            .WithUrlForEndpoint(
                AgenticsTestkitResource.GitEndpointName,
                url => url.DisplayText = "Hosted Git")
            .WithUrlForEndpoint(
                AgenticsTestkitResource.GitSshEndpointName,
                url => url.DisplayText = "Hosted Git SSH")
            .WithEnvironment("AGENTICS_TESTKIT_OWNER", options.Owner)
            .WithEnvironment("AGENTICS_TESTKIT_APP_ID", options.AppId)
            .WithEnvironment("AGENTICS_TESTKIT_APP_NAME", options.AppName)
            .WithEnvironment("AGENTICS_TESTKIT_CLIENT_ID", options.ClientId)
            .WithEnvironment("AGENTICS_TESTKIT_CLIENT_SECRET", options.ClientSecret)
            .WithEnvironment("AGENTICS_TESTKIT_SCOPES", options.Scopes);

        var apiEndpoint = testkit.GetEndpoint(AgenticsTestkitResource.ApiEndpointName);
        var keycloakEndpoint = testkit.GetEndpoint(AgenticsTestkitResource.KeycloakEndpointName);
        var gitEndpoint = testkit.GetEndpoint(AgenticsTestkitResource.GitEndpointName);
        var readyHealthCheck = $"{name}-testkit-ready";
        builder.Services.AddHealthChecks().AddAsyncCheck(
            readyHealthCheck,
            async cancellationToken =>
            {
                try
                {
                    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
                    using var api = await client.GetAsync(
                        $"{apiEndpoint.Url}/api/healthz",
                        cancellationToken);
                    if (!api.IsSuccessStatusCode)
                    {
                        return HealthCheckResult.Unhealthy("Agentics API is not ready.");
                    }

                    using var git = await client.GetAsync(
                        $"{gitEndpoint.Url}/_mgmt/health",
                        cancellationToken);
                    if (!git.IsSuccessStatusCode)
                    {
                        return HealthCheckResult.Unhealthy("Hosted Git is not ready.");
                    }

                    using var token = await client.PostAsync(
                        $"{keycloakEndpoint.Url}/realms/agentics/protocol/openid-connect/token",
                        new FormUrlEncodedContent(new Dictionary<string, string>
                        {
                            ["grant_type"] = "client_credentials",
                            ["client_id"] = options.ClientId,
                            ["client_secret"] = options.ClientSecret,
                        }),
                        cancellationToken);
                    return token.IsSuccessStatusCode
                        ? HealthCheckResult.Healthy()
                        : HealthCheckResult.Unhealthy("Testkit bootstrap is not complete.");
                }
                catch (Exception error)
                {
                    return HealthCheckResult.Unhealthy("Agentics testkit is not ready.", error);
                }
            });
        testkit.WithHealthCheck(readyHealthCheck);
        testkit.WithEnvironment(context =>
        {
            // A normal EndpointReference environment value also adds a resource
            // dependency. On the resource's own endpoint that is a self-cycle,
            // so resolve the already-allocated URLs inside the deferred callback.
            context.EnvironmentVariables["AGENTICS_TESTKIT_BASE_URL"] = apiEndpoint.Url;
            context.EnvironmentVariables["AGENTICS_TESTKIT_KEYCLOAK_PUBLIC_URL"] = keycloakEndpoint.Url;
            context.EnvironmentVariables["AGENTICS_TESTKIT_GIT_PUBLIC_URL"] = gitEndpoint.Url;
            // The testkit's Agentics API persists this URL on project records.
            // Its consumer is another Aspire container (the Coolify emulator),
            // so advertise the stable resource DNS name rather than a browser's
            // allocated localhost port.
            context.EnvironmentVariables["AGENTICS_TESTKIT_GIT_SSH_PUBLIC_URL"] =
                $"ssh://git@{name}:2222";
            context.EnvironmentVariables["AGENTICS_TESTKIT_USERS_JSON"] = JsonSerializer.Serialize(
                testkit.Resource.Users,
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        });

        if (options.PersistData)
        {
            testkit.WithVolume(options.DataVolumeName ?? $"{name}-data", "/testkit-data");
        }

        return testkit;
    }

    /// <summary>Configures the integration application seeded by the testkit.</summary>
    public static IResourceBuilder<AgenticsTestkitResource> WithIntegrationAppRegistration(
        this IResourceBuilder<AgenticsTestkitResource> testkit,
        string owner,
        string appId,
        string appName)
    {
        ArgumentNullException.ThrowIfNull(testkit);
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        ArgumentException.ThrowIfNullOrWhiteSpace(appName);

        var options = testkit.Resource.Options;
        options.Owner = owner;
        options.AppId = appId;
        options.AppName = appName;

        return testkit
            .WithEnvironment("AGENTICS_TESTKIT_OWNER", owner)
            .WithEnvironment("AGENTICS_TESTKIT_APP_ID", appId)
            .WithEnvironment("AGENTICS_TESTKIT_APP_NAME", appName);
    }

    /// <summary>Persists the testkit's Agentics, Keycloak and hosted-Git fixtures.</summary>
    public static IResourceBuilder<AgenticsTestkitResource> WithDataVolume(
        this IResourceBuilder<AgenticsTestkitResource> testkit,
        string? name = null)
    {
        ArgumentNullException.ThrowIfNull(testkit);

        var volumeName = name ?? $"{testkit.Resource.Name}-data";
        ArgumentException.ThrowIfNullOrWhiteSpace(volumeName);

        testkit.Resource.Options.PersistData = true;
        testkit.Resource.Options.DataVolumeName = volumeName;
        return testkit.WithVolume(volumeName, "/testkit-data");
    }

    /// <summary>
    /// Seeds an interactive test-only identity in Keycloak and Agentics. The
    /// user receives Agentics' <c>global-admin</c> realm role, but no implicit
    /// access to private customer projects; integrations must grant that
    /// membership explicitly through the public API.
    /// </summary>
    public static IResourceBuilder<AgenticsTestkitResource> AddAdminUser(
        this IResourceBuilder<AgenticsTestkitResource> testkit,
        string email,
        string name,
        string handle,
        string password = "local-development-only")
    {
        ArgumentNullException.ThrowIfNull(testkit);
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(handle);
        ArgumentException.ThrowIfNullOrWhiteSpace(password);

        if (!email.Contains('@', StringComparison.Ordinal)
            || !IsHandle(handle))
        {
            throw new ArgumentException("Admin users require a valid email and lowercase handle segment.");
        }
        if (testkit.Resource.Users.Any(user =>
                string.Equals(user.Email, email, StringComparison.OrdinalIgnoreCase)
                || string.Equals(user.Handle, handle, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException("Admin user emails and handles must be unique.");
        }

        testkit.Resource.Users.Add(new AgenticsTestkitUser(
            email.Trim().ToLowerInvariant(),
            name.Trim(),
            handle.Trim(),
            password,
            IsAdmin: true));
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

    private static bool IsHandle(string value) =>
        value.Length is > 0 and <= 64
        && (value[0] is >= 'a' and <= 'z' or >= '0' and <= '9')
        && value.All(character =>
            character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');
}
