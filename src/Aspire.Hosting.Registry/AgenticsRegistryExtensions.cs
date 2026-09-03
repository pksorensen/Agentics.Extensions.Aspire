using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting;

/// <summary>
/// Pre-start verification that this machine can pull the AppHost's private
/// container images, with the sign-in steps surfaced in the dashboard when it
/// cannot.
/// </summary>
/// <remarks>
/// The failure this removes: a resource whose image lives on a private
/// registry fails with <c>error from registry: unauthorized</c> several
/// seconds into startup, in one resource's console log, where it reads like
/// the resource is broken rather than like the machine is not signed in.
/// </remarks>
public static class AgenticsRegistryExtensions
{
    private const string CheckCommandName = "check-registry-access";

    /// <summary>
    /// Checks, just before startup, whether this machine can pull the images
    /// the AppHost declares from the Agentics registry — and if it cannot,
    /// says so at the top of the dashboard with the commands that fix it.
    /// </summary>
    /// <remarks>
    /// The check runs in the background: startup is never delayed by it, and a
    /// slow or offline network produces silence rather than a wrong diagnosis.
    /// </remarks>
    /// <example>
    /// <code>
    /// var builder = DistributedApplication.CreateBuilder(args);
    /// builder.UseAgenticsRegistry();
    /// </code>
    /// </example>
    public static IDistributedApplicationBuilder UseAgenticsRegistry(
        this IDistributedApplicationBuilder builder,
        Action<AgenticsRegistryOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = new AgenticsRegistryOptions();
        configure?.Invoke(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Registry);

        builder.Eventing.Subscribe<BeforeStartEvent>((@event, cancellationToken) =>
        {
            var affected = FindAffectedResources(@event.Model, options.Registry);
            var images = affected
                .SelectMany(entry => entry.Images)
                .Concat(options.AdditionalImages)
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            if (images.Length == 0) return Task.CompletedTask;

            var lifetime = @event.Services.GetRequiredService<IHostApplicationLifetime>();
            // Deliberately not awaited. Probing means talking to a registry
            // over the network, and nothing in the app model should wait on
            // that — the resources will fail on their own if the answer is bad,
            // and this exists to explain that failure, not to prevent it.
            _ = Task.Run(
                () => CheckAsync(@event.Services, options, images, affected, lifetime.ApplicationStopping),
                lifetime.ApplicationStopping);

            return Task.CompletedTask;
        });

        return builder;
    }

    /// <summary>
    /// Adds a dashboard command to a resource that re-checks registry access
    /// and starts the resource when it now succeeds. This is the button to
    /// press after signing in, so a resource that failed to pull can recover
    /// without restarting the whole AppHost.
    /// </summary>
    public static IResourceBuilder<T> WithRegistryAccessCheck<T>(
        this IResourceBuilder<T> builder,
        Action<AgenticsRegistryOptions>? configure = null)
        where T : IResource
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = new AgenticsRegistryOptions();
        configure?.Invoke(options);

        return builder.WithCommand(
            CheckCommandName,
            "Check registry access",
            async context =>
            {
                var resource = builder.Resource;
                var images = ImagesFor(resource, options.Registry).ToArray();
                if (images.Length == 0)
                {
                    return CommandResults.Success($"{context.ResourceName} pulls nothing from {options.Registry}.");
                }

                foreach (var image in images)
                {
                    var result = await RegistryAccessProbe
                        .ProbeAsync(image, options.ContainerRuntime, options.Timeout, context.CancellationToken)
                        .ConfigureAwait(false);
                    if (result.State is RegistryAccessState.Reachable) continue;

                    return CommandResults.Failure(result.State is RegistryAccessState.Unauthorized
                        ? $"Not signed in to {options.Registry}. Run `{options.SignInCommand}`, then try this again."
                        : $"Could not check {image}: {result.Detail}");
                }

                var commands = context.ServiceProvider.GetRequiredService<ResourceCommandService>();
                await commands.ExecuteCommandAsync(
                    context.ResourceName,
                    KnownResourceCommands.StartCommand,
                    context.CancellationToken).ConfigureAwait(false);

                return CommandResults.Success($"{options.Registry} is reachable; starting {context.ResourceName}.");
            },
            new CommandOptions
            {
                IconName = "PlugConnected",
                IconVariant = IconVariant.Regular,
                Description = $"Verify this machine can pull from {options.Registry}, then start the resource",
            });
    }

    private sealed record AffectedResource(IResource Resource, IReadOnlyList<string> Images);

    private static List<AffectedResource> FindAffectedResources(DistributedApplicationModel model, string registry)
    {
        var affected = new List<AffectedResource>();
        foreach (var resource in model.Resources)
        {
            var images = ImagesFor(resource, registry).ToArray();
            if (images.Length > 0) affected.Add(new AffectedResource(resource, images));
        }
        return affected;
    }

    private static IEnumerable<string> ImagesFor(IResource resource, string registry)
    {
        foreach (var annotation in resource.Annotations.OfType<ContainerImageAnnotation>())
        {
            var reference = Reference(annotation);
            if (!BelongsTo(reference, registry)) continue;
            yield return reference;
        }
    }

    /// <summary>
    /// Whether an image reference is served by the given registry.
    /// </summary>
    /// <remarks>
    /// Checked on the full reference rather than on
    /// <see cref="ContainerImageAnnotation.Registry"/>, because both spellings
    /// occur in practice: <c>WithImageRegistry("registry.agentics.dk")</c> sets
    /// the field, while <c>WithImage("registry.agentics.dk/agentics/x")</c> —
    /// which is what several of our own hosting extensions do — leaves it null
    /// and puts the host in the image name. Matching only the field silently
    /// skips exactly the resources this check exists for.
    /// </remarks>
    internal static bool BelongsTo(string reference, string registry) =>
        reference.StartsWith(registry + "/", StringComparison.OrdinalIgnoreCase);

    private static string Reference(ContainerImageAnnotation annotation)
    {
        var name = string.IsNullOrEmpty(annotation.Registry)
            ? annotation.Image
            : $"{annotation.Registry}/{annotation.Image}";

        // A digest pin wins over a tag, the same way it does for the runtime.
        if (!string.IsNullOrEmpty(annotation.SHA256)) return $"{name}@sha256:{annotation.SHA256}";
        return string.IsNullOrEmpty(annotation.Tag) ? name : $"{name}:{annotation.Tag}";
    }

    private static async Task CheckAsync(
        IServiceProvider services,
        AgenticsRegistryOptions options,
        IReadOnlyList<string> images,
        IReadOnlyList<AffectedResource> affected,
        CancellationToken cancellationToken)
    {
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("Aspire.Hosting.Registry");

        // One image is enough to answer "is this machine signed in", and the
        // answer applies to the whole registry. Probing all of them would turn
        // a diagnostic into a startup-time network storm.
        var probe = await RegistryAccessProbe
            .ProbeAsync(images[0], options.ContainerRuntime, options.Timeout, cancellationToken)
            .ConfigureAwait(false);

        switch (probe.State)
        {
            case RegistryAccessState.Reachable:
                logger.LogDebug("{Registry} is reachable; {Count} image(s) checked from this AppHost.",
                    options.Registry, images.Count);
                return;

            case RegistryAccessState.Unknown:
                // Not a sign-in problem as far as anyone can tell, so it stays
                // in the log rather than becoming a banner that blames the user.
                logger.LogDebug("Could not determine access to {Registry}: {Detail}", options.Registry, probe.Detail);
                return;
        }

        var names = string.Join(", ", affected.Select(entry => entry.Resource.Name).Distinct(StringComparer.Ordinal));
        logger.LogWarning(
            "Not signed in to {Registry}. {Count} resource(s) pull from it and will fail to start: {Resources}. Run `{Command}` to sign in.",
            options.Registry, affected.Count, names, options.SignInCommand);

        if (!options.Notify) return;
        await NotifyAsync(services, options, names, cancellationToken).ConfigureAwait(false);
    }

    private static async Task NotifyAsync(
        IServiceProvider services,
        AgenticsRegistryOptions options,
        string resourceNames,
        CancellationToken cancellationToken)
    {
        var interactions = services.GetRequiredService<IInteractionService>();
        if (!interactions.IsAvailable) return;

        var summary = string.IsNullOrEmpty(resourceNames)
            ? $"This machine cannot pull from {options.Registry}."
            : $"This machine cannot pull from {options.Registry}, so {resourceNames} will fail to start.";

        try
        {
            var acknowledged = await interactions.PromptNotificationAsync(
                $"Sign in to {options.Registry}",
                $"{summary} Run `{options.SignInCommand}` in a terminal.",
                new NotificationInteractionOptions
                {
                    Intent = MessageIntent.Warning,
                    PrimaryButtonText = "Show me how",
                },
                cancellationToken).ConfigureAwait(false);

            if (acknowledged.Canceled || !acknowledged.Data) return;

            await interactions.PromptMessageBoxAsync(
                $"Sign in to {options.Registry}",
                Steps(options),
                new MessageBoxInteractionOptions
                {
                    Intent = MessageIntent.Information,
                    EnableMessageMarkdown = true,
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            // A non-interactive AppHost client (aspire run in CI, a test host)
            // has no dashboard to prompt. The warning above is the whole
            // diagnostic in that case, which is enough.
        }
    }

    private static string Steps(AgenticsRegistryOptions options) =>
        $"""
        **1. Sign in** — this opens a browser and waits:

        ```
        {options.SignInCommand}
        ```

        Say yes when it offers to register itself as Docker's credential
        helper. Docker has no OAuth of its own, so that helper is how the
        sign-in reaches `docker pull`.

        **2. Start the resources that failed** from their own page in this
        dashboard — the AppHost does not need restarting.

        No `agent-registry` on this machine yet?

        ```
        curl -fsSL https://agentics.dk/install/agent-registry.sh | bash
        ```

        Or, without the helper, a login that lasts until the token expires:

        ```
        docker login {options.Registry} -u oauth2 -p "$(agent-registry token)"
        ```
        """;
}
