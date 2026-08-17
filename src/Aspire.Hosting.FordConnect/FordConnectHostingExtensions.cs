using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.FordConnect;
using System.Diagnostics;
using System.Text.Json;

namespace Aspire.Hosting;

public static class FordConnectHostingExtensions
{
    /// <summary>Builds the emulator from a local checkout instead of pulling the
    /// published image. Same escape hatch the tenant emulator has, for the same
    /// reason: while the emulator itself is what you are changing.</summary>
    public static IResourceBuilder<FordConnectResource> WithLocalEmulatorSource(
        this IResourceBuilder<FordConnectResource> ford,
        string sourceRoot,
        string image = "agentics/fordconnect-emulator",
        string tag = "dev",
        string dockerfilePath = "src/FordConnect.Emulator/Dockerfile")
    {
        ArgumentNullException.ThrowIfNull(ford);
        BuildLocalImage(sourceRoot, dockerfilePath, image, tag, ford.Resource.Name);
        return ford.WithImage(image, tag);
    }

    /// <summary>
    /// Adds an emulated FordConnect 2.0.
    /// </summary>
    /// <remarks>
    /// Pin <see cref="FordConnectOptions.HostPort"/> if the consent flow matters. The
    /// authorize hop happens in a browser and comes back to the app's own callback,
    /// so both ends have to be addresses a browser can reach — which an Aspire-proxied
    /// endpoint on a moving port is not.
    /// </remarks>
    public static IResourceBuilder<FordConnectResource> AddFordConnect(
        this IDistributedApplicationBuilder builder,
        Action<FordConnectOptions>? configure = null,
        string name = "fordconnect")
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var options = new FordConnectOptions();
        configure?.Invoke(options);

        if (options.Mode == FordConnectDataMode.Replay && string.IsNullOrWhiteSpace(options.ReplayPath))
            throw new ArgumentException(
                "Replay mode needs a ReplayPath — a JSONL export of ford.reading events, mounted into the container.",
                nameof(configure));

        var resource = new FordConnectResource(name)
        {
            PublicUrl = options.PublicUrl?.TrimEnd('/')
                ?? (options.HostPort is { } port ? $"http://localhost:{port}" : null),
        };

        var ford = builder.AddResource(resource)
            .WithImage(options.Image, options.Tag)
            .WithHttpEndpoint(
                port: options.HostPort,
                targetPort: 8080,
                name: FordConnectResource.HttpEndpointName,
                // Unproxied when pinned: the proxy is what would make the container
                // see a host the browser never used, and the redirect back to the app
                // would then leave from the wrong origin.
                isProxied: options.HostPort is null)
            .WithHttpHealthCheck("/healthz", endpointName: FordConnectResource.HttpEndpointName)
            .WithUrlForEndpoint(
                FordConnectResource.HttpEndpointName,
                url => url.DisplayText = "FordConnect (emulated)")
            .WithEnvironment(context =>
            {
                context.EnvironmentVariables["FORDCONNECT_SEED_JSON"] = JsonSerializer.Serialize(
                    new FordConnectSeed(
                        resource.ClientId,
                        resource.ClientSecret,
                        resource.Vehicles.ToList(),
                        resource.Owners.ToList(),
                        new FordConnectBehaviour(
                            options.RateLimitBurst,
                            options.RateLimitRefillMs,
                            options.RateLimitAccumulates,
                            options.SendRateLimitHeaders,
                            options.AccessTokenSeconds,
                            options.RotateRefreshToken,
                            resource.ReplayPath is null ? options.Mode.ToString() : nameof(FordConnectDataMode.Replay),
                            resource.ReplayPath ?? options.ReplayPath,
                            resource.ReplaySpeed ?? options.ReplaySpeed)));

                if (resource.PublicUrl is { } publicUrl)
                    context.EnvironmentVariables["FORDCONNECT_PUBLIC_URL"] = publicUrl;
            });

        if (!string.IsNullOrWhiteSpace(options.DockerfileContext))
            ford.WithDockerfile(options.DockerfileContext, options.DockerfilePath);

        if (options.PersistData)
            ford
                .WithVolume(options.DataVolumeName ?? $"{name}-data", "/data")
                .WithEnvironment("FORDCONNECT_DATA_DIR", "/data");

        return ford;
    }

    /// <summary>
    /// Plays back a recorded event log instead of simulating a car.
    ///
    /// <paramref name="hostPath"/> is a file on the developer's machine — a JSONL export
    /// of <c>ford.reading</c> rows — mounted read-only into the container, which is the
    /// half that was missing while <see cref="FordConnectOptions.ReplayPath"/> existed
    /// on its own: a container path with nothing behind it.
    ///
    /// <b>Do not commit the export.</b> A real one carries the full VIN and, since the
    /// log keeps coordinates, the owner's home address. Keep it beside the AppHost in a
    /// git-ignored directory, or hand-write a fixture with a fake VIN.
    /// </summary>
    /// <param name="ford">The emulator resource.</param>
    /// <param name="hostPath">A JSONL export on the developer's machine. Never committed.</param>
    /// <param name="containerPath">Where it is mounted inside the container.</param>
    /// <param name="speed">Wall-clock multiplier. 60 turns an hour of log into a minute,
    /// which is how a cadence experiment becomes a test instead of an afternoon. Gaps
    /// stay gaps: a 42-minute silence becomes 42 seconds of the same reading.</param>
    public static IResourceBuilder<FordConnectResource> WithReplayLog(
        this IResourceBuilder<FordConnectResource> ford,
        string hostPath,
        double speed = 1.0,
        string containerPath = "/replay/ford-events.jsonl")
    {
        ArgumentNullException.ThrowIfNull(ford);
        ArgumentException.ThrowIfNullOrWhiteSpace(hostPath);

        var resolved = Path.GetFullPath(hostPath);
        if (!File.Exists(resolved))
            throw new FileNotFoundException(
                $"No replay log at '{resolved}'. Export one with the platform's own event log — the "
                + "`ford.reading` rows — or hand-write a fixture. An empty replay is indistinguishable from a "
                + "car that never reports.",
                resolved);
        if (speed <= 0)
            throw new ArgumentOutOfRangeException(nameof(speed), speed, "Replay speed must be positive.");

        ford.Resource.ReplayPath = containerPath;
        ford.Resource.ReplaySpeed = speed;

        // Read-only, because the emulator has no business writing to a recording and a
        // container that could would eventually be the reason a log went missing.
        return ford.WithBindMount(resolved, containerPath, isReadOnly: true);
    }

    /// <summary>
    /// Puts a car on the emulated account.
    ///
    /// The VIN is the account key and the <c>vin</c> parameter every per-vehicle
    /// endpoint takes. Keep it obviously fake — a VIN in an AppHost is a VIN in git,
    /// and a real one identifies a real vehicle and its owner.
    /// </summary>
    public static IResourceBuilder<FordConnectResource> AddVehicle(
        this IResourceBuilder<FordConnectResource> ford,
        string vin,
        Action<FordConnectVehicleBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(ford);
        ArgumentException.ThrowIfNullOrWhiteSpace(vin);

        if (ford.Resource.Vehicles.Any(v => string.Equals(v.Vin, vin, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException($"Vehicle '{vin}' is already on this account.", nameof(vin));

        var vehicle = new FordConnectVehicleBuilder(vin);
        configure?.Invoke(vehicle);
        ford.Resource.Vehicles.Add(vehicle.Build());
        return ford;
    }

    /// <summary>The owner who grants consent. The authorize page lists these and
    /// signs in as whichever is clicked; with none seeded it offers one default
    /// owner, so the flow works before anybody configures anything.</summary>
    public static IResourceBuilder<FordConnectResource> AddOwner(
        this IResourceBuilder<FordConnectResource> ford,
        string email,
        string? displayName = null)
    {
        ArgumentNullException.ThrowIfNull(ford);
        ArgumentException.ThrowIfNullOrWhiteSpace(email);

        if (!email.Contains('@'))
            throw new ArgumentException("An owner's email address must contain '@'.", nameof(email));
        if (ford.Resource.Owners.Any(o => string.Equals(o.Email, email, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException($"Owner '{email}' is already seeded.", nameof(email));

        ford.Resource.Owners.Add(new FordConnectOwner(
            Guid.NewGuid().ToString(),
            string.IsNullOrWhiteSpace(displayName) ? email.Split('@')[0] : displayName!,
            email));

        return ford;
    }

    /// <summary>
    /// Points a consuming resource at this emulator, with the five environment
    /// variables a FordConnect client reads.
    ///
    /// This is the whole integration. A client written against real Ford needs no
    /// code change to run against this, because all three of Ford's hosts are already
    /// configuration in any client that got the auth flow right — Ford's own front
    /// door, its B2C token endpoint and its Query API are three different URL prefixes
    /// on one hostname, and hard-coding them is the mistake this shape prevents.
    /// </summary>
    public static IResourceBuilder<T> WithFordConnect<T>(
        this IResourceBuilder<T> app,
        IResourceBuilder<FordConnectResource> ford,
        string? redirectUri = null)
        where T : IResourceWithEnvironment, IResourceWithWaitSupport
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(ford);

        var baseUrl = ford.Resource.PublicUrl
            ?? throw new InvalidOperationException(
                "This FordConnect emulator has no public address, so no client can be pointed at it. Set "
                + "FordConnectOptions.HostPort (or PublicUrl) when adding it — the consent hop happens in a browser "
                + "and has to leave from an address the browser used.");

        return app
            .WithEnvironment("FORD_CLIENT_ID", ford.Resource.ClientId)
            .WithEnvironment("FORD_CLIENT_SECRET", ford.Resource.ClientSecret)
            // The real value carries `?locale=da_DK`, and without it Ford answers
            // `400 Invalid locale: _`. Kept here so a client that strips query
            // parameters off the authorize URL fails locally too.
            .WithEnvironment("FORD_AUTHORIZE_URL", $"{baseUrl}/fcon-public/v1/auth/init?locale=da_DK")
            .WithEnvironment("FORD_TOKEN_URL",
                $"{baseUrl}/dah2vb2cprod.onmicrosoft.com/oauth2/v2.0/token?p=B2C_1A_FCON_AUTHORIZE")
            .WithEnvironment("FORD_QUERY_BASE", $"{baseUrl}/fcon-query/v1")
            .WithEnvironment("FORD_SCOPES", $"{ford.Resource.ClientId} openid offline_access")
            .WithEnvironment(context =>
            {
                if (redirectUri is { Length: > 0 })
                    context.EnvironmentVariables["FORD_REDIRECT_URI"] = redirectUri;
            })
            .WaitFor(ford);
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

/// <summary>Mutable builder for a seeded car, so the AppHost sets two fields without
/// restating the other ten.</summary>
public sealed class FordConnectVehicleBuilder
{
    private readonly string _vin;

    internal FordConnectVehicleBuilder(string vin) => _vin = vin;

    public string? NickName { get; set; }
    public string ModelName { get; set; } = "Mustang Mach-E";
    public string ModelYear { get; set; } = "2022";
    public string Color { get; set; } = "Space White";
    public string EngineType { get; set; } = "BEV";
    public double StateOfCharge { get; set; } = 76.5;
    public double CapacityKwh { get; set; } = 88.0;
    public double Odometer { get; set; } = 99_878;

    /// <summary>Where the car starts. Copenhagen city hall by default — a real
    /// coordinate would be somebody's home, and this one is a landmark.</summary>
    public double Latitude { get; set; } = 55.6761;
    public double Longitude { get; set; } = 12.5683;

    internal FordConnectVehicle Build() => new(
        _vin, NickName, ModelName, ModelYear, Color, EngineType,
        StateOfCharge, CapacityKwh, Odometer, Latitude, Longitude);
}
