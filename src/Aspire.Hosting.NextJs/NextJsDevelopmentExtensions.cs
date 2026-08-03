using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.JavaScript;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting;

/// <summary>
/// Local-development diagnostics and dashboard actions for Next.js resources.
/// </summary>
public static class NextJsDevelopmentExtensions
{
    private const string ClearCommandName = "clear-turbopack-cache";

    /// <summary>
    /// Turns off Next.js' anonymous usage telemetry by setting
    /// <c>NEXT_TELEMETRY_DISABLED=1</c>.
    /// </summary>
    /// <remarks>
    /// Beyond the privacy question, this removes a real local-development
    /// failure mode. On exit a Next.js process spawns a detached
    /// <c>detached-flush.js</c> child to post its telemetry batch, so the
    /// command itself does not wait on the network. That child is supposed to
    /// live for seconds; when the upload neither completes nor fails — no
    /// egress, a black-holed connection — it has been observed spinning at
    /// ~100% CPU for hours, and every restart leaves another one behind.
    /// Disabling telemetry means the flusher is never spawned.
    /// </remarks>
    public static IResourceBuilder<TResource> DisableTelemetry<TResource>(
        this IResourceBuilder<TResource> builder)
        where TResource : JavaScriptAppResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.WithEnvironment("NEXT_TELEMETRY_DISABLED", "1");
    }

    /// <summary>
    /// Enables Next.js' detailed Turbopack trace at <c>.next/dev/trace-turbopack</c>.
    /// The trace can be opened with <c>npx next internal trace .next/dev/trace-turbopack</c>.
    /// </summary>
    public static IResourceBuilder<TResource> WithTurbopackTracing<TResource>(
        this IResourceBuilder<TResource> builder)
        where TResource : JavaScriptAppResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.WithEnvironment("NEXT_TURBOPACK_TRACING", "1");
    }

    /// <summary>
    /// Adds a dashboard command that safely stops the resource, clears its
    /// Turbopack cache, and restarts it. At AppHost startup the cache is cleared
    /// automatically when it meets or exceeds <paramref name="limit"/>.
    /// </summary>
    public static IResourceBuilder<TResource> ClearTurbopackCache<TResource>(
        this IResourceBuilder<TResource> builder,
        string limit = "1gb")
        where TResource : JavaScriptAppResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        var annotation = GetOrCreateAnnotation(builder);
        annotation.AutomaticClearLimitBytes = ByteSize.Parse(limit);
        annotation.CacheWarningLimitBytes = annotation.AutomaticClearLimitBytes.Value;

        EnsureClearCommand(builder, annotation);
        if (annotation.AutomaticClearAdded) return builder;
        annotation.AutomaticClearAdded = true;

        builder.ApplicationBuilder.Eventing.Subscribe<BeforeStartEvent>(async (@event, cancellationToken) =>
        {
            var cacheBytes = TurbopackCache.GetSize(annotation.WorkingDirectory);
            if (cacheBytes < annotation.AutomaticClearLimitBytes) return;

            var logger = @event.Services
                .GetRequiredService<ResourceLoggerService>()
                .GetLogger(annotation.Resource.Name);
            logger.LogWarning(
                "Turbopack cache reached {CacheSize}, above the configured {CacheLimit} limit; clearing before startup.",
                ByteSize.Format(cacheBytes),
                ByteSize.Format(annotation.AutomaticClearLimitBytes.Value));

            await Task.Run(
                () => TurbopackCache.Clear(annotation.WorkingDirectory, logger),
                cancellationToken).ConfigureAwait(false);
            annotation.Reset();
        });

        return builder;
    }

    /// <summary>
    /// Watches Next.js' high-level development trace for slow compile spans and
    /// the Turbopack cache for excessive growth. Warnings appear in the Aspire
    /// notification center and link back to the resource's highlighted clear-cache command.
    /// </summary>
    public static IResourceBuilder<TResource> WithSlowStartDetector<TResource>(
        this IResourceBuilder<TResource> builder,
        Action<TurbopackSlowStartDetectorOptions>? configure = null)
        where TResource : JavaScriptAppResource
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = new TurbopackSlowStartDetectorOptions();
        configure?.Invoke(options);
        Validate(options);

        var annotation = GetOrCreateAnnotation(builder);
        annotation.SlowCompilationThreshold = options.SlowCompilationThreshold;
        annotation.PollInterval = options.PollInterval;
        annotation.CacheSizePollInterval = options.CacheSizePollInterval;
        annotation.CacheWarningLimitBytes = ByteSize.Parse(options.CacheWarningLimit);

        EnsureClearCommand(builder, annotation);
        if (annotation.SlowStartDetectorAdded) return builder;
        annotation.SlowStartDetectorAdded = true;

        builder.OnResourceReady((_, @event, _) =>
        {
            if (!annotation.TryStartMonitor()) return Task.CompletedTask;

            var lifetime = @event.Services.GetRequiredService<IHostApplicationLifetime>();
            _ = Task.Run(
                () => MonitorAsync(annotation, @event.Services, lifetime.ApplicationStopping),
                lifetime.ApplicationStopping);
            return Task.CompletedTask;
        });

        return builder;
    }

    private static async Task MonitorAsync(
        TurbopackDiagnosticsAnnotation annotation,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        var logger = services
            .GetRequiredService<ResourceLoggerService>()
            .GetLogger(annotation.Resource.Name);

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var warning = await annotation.InspectAsync(cancellationToken).ConfigureAwait(false);
                if (warning is not null)
                {
                    await PublishWarningAsync(annotation, warning, services, logger, cancellationToken)
                        .ConfigureAwait(false);
                }

                await Task.Delay(annotation.PollInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception error)
            {
                logger.LogDebug(error, "Turbopack slow-start detector could not inspect this interval.");
                await Task.Delay(annotation.PollInterval, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static async Task PublishWarningAsync(
        TurbopackDiagnosticsAnnotation annotation,
        TurbopackWarning warning,
        IServiceProvider services,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        logger.LogWarning(
            "{Summary}. {Detail} Cache size: {CacheSize}. Use the '{Command}' resource command to reset it.",
            warning.Summary,
            warning.Detail,
            ByteSize.Format(warning.CacheBytes),
            ClearCommandName);

        var notifications = services.GetRequiredService<ResourceNotificationService>();
        await notifications.PublishUpdateAsync(annotation.Resource, snapshot => snapshot with
        {
            Properties =
            [
                .. snapshot.Properties.Where(property =>
                    property.Name is not ("Turbopack cache" or "Turbopack warning")),
                new ResourcePropertySnapshot("Turbopack cache", ByteSize.Format(warning.CacheBytes)),
                new ResourcePropertySnapshot("Turbopack warning", $"{warning.Summary}. {warning.Detail}"),
            ],
        }).ConfigureAwait(false);

        if (!annotation.TryMarkWarningPublished()) return;

        var interactions = services.GetRequiredService<IInteractionService>();
        if (!interactions.IsAvailable) return;

        try
        {
            await interactions.PromptNotificationAsync(
                "Slow Next.js development startup",
                $"{annotation.Resource.Name}: {warning.Summary}. {warning.Detail} " +
                $"Cache: {ByteSize.Format(warning.CacheBytes)}. Use Clear Turbopack cache on the resource.",
                new NotificationInteractionOptions
                {
                    Intent = MessageIntent.Warning,
                    LinkText = "Open resource commands",
                    LinkUrl = $"/?resource={Uri.EscapeDataString(annotation.Resource.Name)}",
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            // Non-interactive AppHost clients do not expose the dashboard
            // interaction service. The resource log and properties still carry
            // the complete diagnostic and action name.
        }
    }

    private static void EnsureClearCommand<TResource>(
        IResourceBuilder<TResource> builder,
        TurbopackDiagnosticsAnnotation annotation)
        where TResource : JavaScriptAppResource
    {
        if (annotation.ClearCommandAdded) return;
        annotation.ClearCommandAdded = true;

        builder.WithCommand(
            ClearCommandName,
            "Clear Turbopack cache",
            async context =>
            {
                try
                {
                    var commands = context.ServiceProvider.GetRequiredService<ResourceCommandService>();
                    await commands.ExecuteCommandAsync(
                        context.ResourceName,
                        KnownResourceCommands.StopCommand,
                        context.CancellationToken).ConfigureAwait(false);

                    var clearedBytes = await Task.Run(
                        () => TurbopackCache.Clear(annotation.WorkingDirectory, context.Logger),
                        context.CancellationToken).ConfigureAwait(false);
                    annotation.Reset();

                    await commands.ExecuteCommandAsync(
                        context.ResourceName,
                        KnownResourceCommands.StartCommand,
                        context.CancellationToken).ConfigureAwait(false);

                    return CommandResults.Success(
                        $"Cleared {ByteSize.Format(clearedBytes)} of Turbopack cache and restarted {context.ResourceName}.");
                }
                catch (Exception error)
                {
                    return CommandResults.Failure(error);
                }
            },
            new CommandOptions
            {
                IconName = "Broom",
                IconVariant = IconVariant.Regular,
                Description = "Stop Next.js, delete its generated Turbopack cache, and restart it",
                ConfirmationMessage = "Clear the generated Turbopack cache and restart this resource?",
                IsHighlighted = true,
            });
    }

    private static TurbopackDiagnosticsAnnotation GetOrCreateAnnotation<TResource>(
        IResourceBuilder<TResource> builder)
        where TResource : JavaScriptAppResource
    {
        if (builder.Resource.TryGetLastAnnotation<TurbopackDiagnosticsAnnotation>(out var annotation))
            return annotation;

        annotation = new TurbopackDiagnosticsAnnotation(builder.Resource);
        builder.WithAnnotation(annotation);
        return annotation;
    }

    private static void Validate(TurbopackSlowStartDetectorOptions options)
    {
        if (options.SlowCompilationThreshold <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options.SlowCompilationThreshold));
        if (options.PollInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options.PollInterval));
        if (options.CacheSizePollInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options.CacheSizePollInterval));
    }
}
