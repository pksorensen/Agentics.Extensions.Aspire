using System.Diagnostics;
using System.Text.Json;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Foundry;
using Aspire.Hosting.MicrosoftTenant;

namespace Aspire.Hosting;

public static class FoundryHostingExtensions
{
    /// <summary>
    /// Adds an emulated Azure AI Foundry: OpenAI-compatible inference
    /// (<c>/openai/v1/responses</c>, <c>/openai/v1/chat/completions</c>, the dated
    /// <c>/openai/deployments/{d}/chat/completions</c>), scripted playback, a usage
    /// log at <c>/_emulator/usage</c> and an ARM surface at <c>/arm</c>.
    /// </summary>
    /// <remarks>
    /// Pin <see cref="FoundryOptions.HostPort"/> when a tool outside the AppHost —
    /// Codex in a terminal — needs a base URL that survives a restart.
    /// </remarks>
    public static IResourceBuilder<FoundryResource> AddFoundryEmulator(
        this IDistributedApplicationBuilder builder,
        string name = "foundry",
        Action<FoundryOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var options = new FoundryOptions();
        configure?.Invoke(options);

        var resource = new FoundryResource(name)
        {
            PublicUrl = options.PublicUrl?.TrimEnd('/') ?? (options.HostPort is { } port ? $"http://localhost:{port}" : null),
            Behaviour = new FoundryBehaviour(
                RequireAuth: options.RequireAuth,
                AllowSimKeys: options.AllowSimKeys,
                DefaultChunkSize: options.ChunkSize,
                DefaultDelayMs: options.ChunkDelayMs),
        };

        var foundry = builder.AddResource(resource)
            .WithImage(options.Image, options.Tag)
            .WithHttpEndpoint(port: options.HostPort, targetPort: 8080, name: FoundryResource.HttpEndpointName,
                isProxied: options.HostPort is null)
            .WithHttpHealthCheck("/healthz", endpointName: FoundryResource.HttpEndpointName)
            .WithUrlForEndpoint(FoundryResource.HttpEndpointName, url => url.DisplayText = "Foundry (emulated)")
            .WithEnvironment(context =>
            {
                context.EnvironmentVariables["FOUNDRY_EMULATOR_JSON"] = JsonSerializer.Serialize(new FoundrySeed(
                    resource.Accounts.ToList(),
                    resource.Usage.ToList(),
                    resource.Callers.ToList(),
                    resource.Behaviour,
                    resource.GatewayKeys.ToList()));
                if (resource.PublicUrl is { } publicUrl)
                    context.EnvironmentVariables["FOUNDRY_PUBLIC_URL"] = publicUrl;
            });

        if (!string.IsNullOrWhiteSpace(options.DockerfileContext))
            foundry.WithDockerfile(options.DockerfileContext, options.DockerfilePath);

        if (options.PersistData)
            foundry.WithVolume(options.DataVolumeName ?? $"{name}-data", "/data")
                   .WithEnvironment("FOUNDRY_DATA_DIR", "/data");

        return foundry;
    }

    /// <summary>Builds the emulator image from a local checkout instead of pulling it.</summary>
    public static IResourceBuilder<FoundryResource> WithLocalEmulatorSource(
        this IResourceBuilder<FoundryResource> foundry,
        string sourceRoot,
        string image = "agentics/foundry-emulator",
        string tag = "dev",
        string dockerfilePath = "src/Foundry.Emulator/Dockerfile")
    {
        ArgumentNullException.ThrowIfNull(foundry);
        BuildLocalImage(sourceRoot, dockerfilePath, image, tag, foundry.Resource.Name);
        return foundry.WithImage(image, tag);
    }

    /// <summary>
    /// Accepts Entra bearer tokens minted by the tenant emulator: a bearer that is not
    /// an account or gateway key is introspected at the tenant's <c>tokeninfo</c>
    /// endpoint, and the token's app id or user becomes the caller in the usage log.
    /// </summary>
    public static IResourceBuilder<FoundryResource> WithMicrosoftTenant(
        this IResourceBuilder<FoundryResource> foundry,
        IResourceBuilder<MicrosoftTenantResource> tenant)
    {
        ArgumentNullException.ThrowIfNull(foundry);
        ArgumentNullException.ThrowIfNull(tenant);
        var endpoint = tenant.GetEndpoint(MicrosoftTenantResource.HttpEndpointName);
        return foundry
            .WithEnvironment("FOUNDRY_TOKENINFO_URL",
                ReferenceExpression.Create($"{endpoint}/{tenant.Resource.TenantId}/oauth2/v2.0/tokeninfo"))
            .WaitFor(tenant);
    }

    /// <summary>Mounts a directory of scenario files (<c>*.json</c>, <c>*.yaml</c>)
    /// read-only. Files are read at startup; <c>PUT /_emulator/scenarios/{name}</c>
    /// adds more at runtime.</summary>
    public static IResourceBuilder<FoundryResource> WithScenarios(
        this IResourceBuilder<FoundryResource> foundry,
        string hostPath,
        string containerPath = "/scenarios")
    {
        ArgumentNullException.ThrowIfNull(foundry);
        ArgumentException.ThrowIfNullOrWhiteSpace(hostPath);
        var full = Path.IsPathRooted(hostPath)
            ? hostPath
            : Path.GetFullPath(Path.Combine(foundry.ApplicationBuilder.AppHostDirectory, hostPath));
        return foundry.WithBindMount(full, containerPath, isReadOnly: true)
                      .WithEnvironment("FOUNDRY_SCENARIOS_DIR", containerPath);
    }

    /// <summary>Adds a Foundry / Azure OpenAI account with its deployments and keys.</summary>
    public static IResourceBuilder<FoundryResource> WithAccount(
        this IResourceBuilder<FoundryResource> foundry,
        string name,
        Action<FoundryAccountBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(foundry);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var account = new FoundryAccountBuilder(name);
        configure?.Invoke(account);
        foundry.Resource.Accounts.Add(account.Build());
        return foundry;
    }

    /// <summary>
    /// A key that is not an account key — what an API gateway subscription key is to a
    /// gateway in front of Foundry. Usage made with it is attributed to
    /// <paramref name="customer"/> and, when given, the subscription / user / initiative.
    /// Accepted in <c>api-key</c>, <c>Ocp-Apim-Subscription-Key</c> or as a bearer.
    /// </summary>
    public static IResourceBuilder<FoundryResource> WithGatewayKey(
        this IResourceBuilder<FoundryResource> foundry,
        string key,
        string customer,
        string? subscriptionId = null,
        string? userPrincipal = null,
        string? initiative = null,
        string? project = null)
    {
        ArgumentNullException.ThrowIfNull(foundry);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(customer);
        foundry.Resource.GatewayKeys.Add(new FoundryApiKeySeed(subscriptionId ?? customer, key, customer, project,
            subscriptionId, userPrincipal, initiative));
        return foundry;
    }

    /// <summary>Attributes calls from an Entra principal (app id or user object id) to a
    /// customer and project.</summary>
    public static IResourceBuilder<FoundryResource> WithCaller(
        this IResourceBuilder<FoundryResource> foundry,
        string callerId,
        string? customer,
        string? project = null,
        string? displayName = null)
    {
        ArgumentNullException.ThrowIfNull(foundry);
        foundry.Resource.Callers.Add(new FoundryCallerSeed(callerId, customer, project, displayName));
        return foundry;
    }

    /// <summary>
    /// Generates <paramref name="days"/> of deterministic historic usage ending today —
    /// weekday-shaped, jittered with a fixed seed so every run renders the same chart.
    /// Regenerated at every start and never written to the usage log.
    /// </summary>
    public static IResourceBuilder<FoundryResource> WithSeededUsage(
        this IResourceBuilder<FoundryResource> foundry,
        string account,
        string deployment,
        string customer,
        int days = 30,
        long dailyInputTokens = 1_000_000,
        long dailyOutputTokens = 100_000,
        int dailyRequests = 200,
        Action<FoundryUsageSeedBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(foundry);
        var seed = new FoundryUsageSeedBuilder();
        configure?.Invoke(seed);
        foundry.Resource.Usage.Add(new FoundryUsageSeed(account, deployment, customer, seed.Project, seed.Caller ?? customer,
            days, dailyInputTokens, dailyOutputTokens, dailyRequests, seed.RandomSeed ?? HashCode(customer + deployment),
            seed.CachedRatio, null, seed.SubscriptionId, seed.UserPrincipal, seed.Initiative));
        return foundry;
    }

    /// <summary>Points an app at one account: <c>AZURE_OPENAI_ENDPOINT</c>,
    /// <c>AZURE_OPENAI_API_KEY</c>, <c>OPENAI_BASE_URL</c> (v1) and, when
    /// <paramref name="deployment"/> is set, <c>AZURE_OPENAI_DEPLOYMENT</c>.
    /// Env names can be prefixed for apps that read their own.</summary>
    public static IResourceBuilder<T> WithFoundryEmulator<T>(
        this IResourceBuilder<T> app,
        IResourceBuilder<FoundryResource> foundry,
        string account,
        string? deployment = null,
        string envPrefix = "")
        where T : IResourceWithEnvironment, IResourceWithWaitSupport
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(foundry);
        var seed = foundry.Resource.Accounts.FirstOrDefault(a => a.Name == account)
            ?? throw new ArgumentException($"Account '{account}' has not been added with WithAccount.", nameof(account));
        var endpoint = foundry.GetEndpoint(FoundryResource.HttpEndpointName);
        app.WithEnvironment($"{envPrefix}AZURE_OPENAI_ENDPOINT", ReferenceExpression.Create($"{endpoint}/{account}/"))
           .WithEnvironment($"{envPrefix}OPENAI_BASE_URL", ReferenceExpression.Create($"{endpoint}/{account}/openai/v1"))
           .WithEnvironment($"{envPrefix}FOUNDRY_ARM_ENDPOINT", ReferenceExpression.Create($"{endpoint}/arm"));
        var key = seed.Keys.FirstOrDefault()?.Value;
        if (key is not null) app.WithEnvironment($"{envPrefix}AZURE_OPENAI_API_KEY", key);
        if (deployment is not null) app.WithEnvironment($"{envPrefix}AZURE_OPENAI_DEPLOYMENT", deployment);
        return app.WaitFor(foundry);
    }

    private static int HashCode(string value)
    {
        unchecked
        {
            var hash = 17;
            foreach (var c in value) hash = hash * 31 + c;
            return hash & 0x7fffffff;
        }
    }

    private static void BuildLocalImage(string sourceRoot, string dockerfilePath, string image, string tag, string resourceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(dockerfilePath);

        var context = Path.GetFullPath(sourceRoot);
        var dockerfile = Path.IsPathRooted(dockerfilePath) ? dockerfilePath : Path.Combine(context, dockerfilePath);
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
                throw new InvalidOperationException($"Docker exited with code {process.ExitCode} while building '{imageReference}'.");
        }
        catch (Exception error) when (error is not InvalidOperationException)
        {
            throw new InvalidOperationException($"Failed to build local image '{imageReference}' for '{resourceName}'.", error);
        }
    }
}

/// <summary>Mutable builder for a seeded account.</summary>
public sealed class FoundryAccountBuilder
{
    private readonly string _name;
    private readonly List<FoundryDeploymentSeed> _deployments = [];
    private readonly List<FoundryApiKeySeed> _keys = [];

    internal FoundryAccountBuilder(string name) => _name = name;

    /// <summary>Fictional by default. Use the ids your app's demo mode already uses so
    /// ARM lookups line up.</summary>
    public string SubscriptionId { get; set; } = "00000000-0000-4000-8000-0000000f0001";
    public string ResourceGroup { get; set; } = "rg-foundry-emulator";
    public string Location { get; set; } = "swedencentral";
    /// <summary><c>AIServices</c> (Foundry) or <c>OpenAI</c> (classic Azure OpenAI).</summary>
    public string Kind { get; set; } = "AIServices";
    public Dictionary<string, string> Tags { get; } = [];
    public List<string> Projects { get; } = [];
    /// <summary>Extra path segments that route to this account — e.g. a gateway's
    /// business-line prefix, so <c>{emulator}/{line}/openai/v1</c> works.</summary>
    public List<string> Aliases { get; } = [];

    public FoundryAccountBuilder WithDeployment(
        string name,
        string? model = null,
        string version = "1",
        string sku = "GlobalStandard",
        int capacity = 100,
        string? scenario = null,
        params string[] aliases)
    {
        _deployments.Add(new FoundryDeploymentSeed(name, model ?? name, version, "OpenAI", sku, capacity, scenario, null,
            aliases.Length == 0 ? null : aliases));
        return this;
    }

    /// <summary>Sets <c>key1</c> (and optionally <c>key2</c>). Without it both are
    /// derived from the account name — stable, but not secret.</summary>
    public FoundryAccountBuilder WithKeys(string key1, string? key2 = null)
    {
        _keys.Add(new FoundryApiKeySeed("key1", key1));
        if (key2 is not null) _keys.Add(new FoundryApiKeySeed("key2", key2));
        return this;
    }

    internal FoundryAccountSeed Build() => new(_name, SubscriptionId, ResourceGroup, Location, Kind, _deployments.ToList(),
        _keys.ToList(), Tags.Count == 0 ? null : new Dictionary<string, string>(Tags),
        Projects.Count == 0 ? null : Projects.ToList(), Aliases.Count == 0 ? null : Aliases.ToList());
}

public sealed class FoundryUsageSeedBuilder
{
    public string? Project { get; set; }
    public string? Caller { get; set; }
    public string? SubscriptionId { get; set; }
    public string? UserPrincipal { get; set; }
    public string? Initiative { get; set; }
    public double CachedRatio { get; set; }
    public int? RandomSeed { get; set; }
}
