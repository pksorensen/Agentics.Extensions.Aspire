using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.MicrosoftTenant;
using System.Diagnostics;
using System.Text.Json;

namespace Aspire.Hosting;

public static class MicrosoftTenantHostingExtensions
{
    /// <summary>
    /// Builds the tenant emulator from a local checkout and makes this resource use that image.
    /// Published consumers can omit this and use the package's GHCR image instead.
    /// </summary>
    public static IResourceBuilder<MicrosoftTenantResource> WithLocalEmulatorSource(
        this IResourceBuilder<MicrosoftTenantResource> tenant,
        string sourceRoot,
        string image = "agentics/microsoft-tenant-emulator",
        string tag = "dev",
        string dockerfilePath = "src/MicrosoftTenant.Emulator/Dockerfile")
    {
        ArgumentNullException.ThrowIfNull(tenant);
        BuildLocalImage(sourceRoot, dockerfilePath, image, tag, tenant.Resource.Name);
        return tenant.WithImage(image, tag);
    }

    public static IResourceBuilder<MicrosoftTenantResource> AddMicrosoftTenant(
        this IDistributedApplicationBuilder builder,
        string primaryDomain,
        string tenantId,
        Action<MicrosoftTenantOptions>? configure = null,
        string name = "microsoft-tenant")
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(primaryDomain);
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (!Guid.TryParse(tenantId, out _))
            throw new ArgumentException("A Microsoft tenant id must be a GUID.", nameof(tenantId));

        var options = new MicrosoftTenantOptions();
        configure?.Invoke(options);

        var resource = new MicrosoftTenantResource(name, primaryDomain, tenantId)
        {
            // The address clients will use. Explicit wins; otherwise a fixed host
            // port implies it. Neither set means no sign-in flow — see the note
            // on MicrosoftTenantOptions.HostPort for why the issuer cannot be
            // discovered from the request when a proxy sits in front.
            PublicUrl = options.PublicUrl?.TrimEnd('/')
                ?? (options.HostPort is { } port ? $"http://localhost:{port}" : null),
        };

        var tenant = builder.AddResource(resource)
            .WithImage(options.Image, options.Tag)
            .WithHttpEndpoint(
                port: options.HostPort,
                targetPort: 8080,
                name: MicrosoftTenantResource.HttpEndpointName,
                // Unproxied when the port is pinned: the proxy is what would make
                // the container see a different host than the client used.
                isProxied: options.HostPort is null)
            .WithHttpHealthCheck("/healthz", endpointName: MicrosoftTenantResource.HttpEndpointName)
            .WithUrlForEndpoint(
                MicrosoftTenantResource.HttpEndpointName,
                url => url.DisplayText = $"Microsoft tenant ({primaryDomain})")
            .WithEnvironment("MICROSOFT_TENANT_DOMAIN", primaryDomain)
            .WithEnvironment("MICROSOFT_TENANT_ID", tenantId)
            .WithEnvironment(context =>
            {
                context.EnvironmentVariables["MICROSOFT_TENANT_SEED_JSON"] =
                    JsonSerializer.Serialize(new MicrosoftTenantSeed(
                        resource.PrimaryDomain,
                        resource.TenantId,
                        resource.Applications.ToList(),
                        resource.Users.ToList()));

                if (resource.PublicUrl is { } publicUrl)
                    context.EnvironmentVariables["MICROSOFT_TENANT_PUBLIC_URL"] = publicUrl;

                foreach (var feature in resource.Features)
                {
                    context.EnvironmentVariables[feature.Key] = JsonSerializer.Serialize(feature.Value);
                }
            });

        if (!string.IsNullOrWhiteSpace(options.DockerfileContext))
            tenant.WithDockerfile(options.DockerfileContext, options.DockerfilePath);

        if (options.PersistData)
            tenant
                .WithVolume(options.DataVolumeName ?? $"{name}-data", "/data")
                .WithEnvironment("USER_DATA_DIR", "/data");

        return tenant;
    }

    public static IResourceBuilder<MicrosoftTenantResource> AddAppRegistration(
        this IResourceBuilder<MicrosoftTenantResource> tenant,
        string displayName,
        string clientId,
        string clientSecret)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientSecret);

        if (!Guid.TryParse(clientId, out _))
            throw new ArgumentException("An application client id must be a GUID.", nameof(clientId));
        if (tenant.Resource.Applications.Any(app => app.ClientId == clientId))
            throw new ArgumentException($"Application '{clientId}' is already seeded.", nameof(clientId));

        tenant.Resource.Applications.Add(new(displayName, clientId, clientSecret));
        return tenant;
    }

    /// <summary>
    /// Seeds a person into the emulated directory, with the app roles they hold.
    ///
    /// Roles are opaque strings — whatever the application under test expects on
    /// the <c>roles</c> claim. The emulator never interprets them, so nothing
    /// about one product's role names has to be known here.
    ///
    /// Seeding is not required to sign in: the sign-in page also accepts an
    /// address it has never seen, which is what makes "invite somebody, then be
    /// them" a flow a test can walk end to end.
    /// </summary>
    public static IResourceBuilder<MicrosoftTenantResource> AddUser(
        this IResourceBuilder<MicrosoftTenantResource> tenant,
        string email,
        string? displayName = null,
        params string[] roles)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentException.ThrowIfNullOrWhiteSpace(email);

        if (!email.Contains('@'))
            throw new ArgumentException("A user's email address must contain '@'.", nameof(email));
        if (tenant.Resource.Users.Any(u => string.Equals(u.Email, email, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException($"User '{email}' is already seeded.", nameof(email));

        tenant.Resource.Users.Add(new MicrosoftTenantUser(
            MicrosoftTenant.Emulator.MicrosoftTenantState.ObjectIdFor(email),
            string.IsNullOrWhiteSpace(displayName) ? email.Split('@')[0] : displayName!,
            email,
            roles));

        return tenant;
    }

    /// <summary>
    /// The OIDC authority this tenant issues tokens under —
    /// <c>{public url}/{tenant id}/v2.0</c>, the value an application's issuer
    /// setting has to match byte for byte.
    /// </summary>
    public static string GetIssuer(this IResourceBuilder<MicrosoftTenantResource> tenant)
    {
        ArgumentNullException.ThrowIfNull(tenant);

        var publicUrl = tenant.Resource.PublicUrl
            ?? throw new InvalidOperationException(
                "This tenant has no public address, so no issuer can be stated. Set MicrosoftTenantOptions.HostPort "
                + "(or PublicUrl) when adding it — an OIDC client compares the issuer byte for byte and will refuse "
                + "a sign-in when it differs.");

        return $"{publicUrl}/{tenant.Resource.TenantId}/v2.0";
    }

    private static void BuildLocalImage(
        string sourceRoot,
        string dockerfilePath,
        string image,
        string tag,
        string resourceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(dockerfilePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(image);
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);

        var context = Path.GetFullPath(sourceRoot);
        var dockerfile = Path.IsPathRooted(dockerfilePath)
            ? dockerfilePath
            : Path.Combine(context, dockerfilePath);
        if (!Directory.Exists(context))
            throw new DirectoryNotFoundException($"Local source directory '{context}' does not exist.");
        if (!File.Exists(dockerfile))
            throw new FileNotFoundException($"Local Dockerfile '{dockerfile}' does not exist.", dockerfile);

        var imageReference = $"{image}:{tag}";
        Console.WriteLine($"[{resourceName}] building local image {imageReference}");
        try
        {
            using var process = Process.Start(new ProcessStartInfo("docker")
            {
                WorkingDirectory = context,
                ArgumentList = { "build", "--file", dockerfile, "--tag", imageReference, context },
            }) ?? throw new InvalidOperationException("Docker could not be started.");
            process.WaitForExit();
            if (process.ExitCode != 0)
                throw new InvalidOperationException(
                    $"Docker exited with code {process.ExitCode} while building '{imageReference}'.");
        }
        catch (Exception error) when (error is not InvalidOperationException)
        {
            throw new InvalidOperationException(
                $"Failed to build local image '{imageReference}' for '{resourceName}'.", error);
        }
    }
}
