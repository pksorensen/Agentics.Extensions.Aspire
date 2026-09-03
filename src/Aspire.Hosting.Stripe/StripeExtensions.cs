using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Stripe;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting;

/// <summary>
/// Adds the Stripe CLI to an Aspire AppHost as managed webhook listeners.
/// </summary>
public static class StripeExtensions
{
    /// <summary>
    /// Adds Stripe CLI webhook listeners and hands the signing secret to
    /// <paramref name="target"/>.
    /// <para>
    /// Two listeners are added, because a Connect integration needs both streams and one
    /// <c>stripe listen</c> only forwards one of them:
    /// </para>
    /// <list type="bullet">
    ///   <item><description><c>stripe-webhooks</c> → <c>--forward-to</c> the app's webhook path.</description></item>
    ///   <item><description><c>stripe-connect-webhooks</c> → <c>--forward-connect-to</c> the Connect path.</description></item>
    /// </list>
    /// <para>
    /// The CLI itself is resolved before anything starts: an explicit path, then
    /// <c>STRIPE_CLI_BIN</c>, then a previous download, then PATH — and failing all of those
    /// it is downloaded from Stripe's GitHub releases and checksum-verified, so a fresh
    /// machine does not meet <c>executable file not found in $PATH</c>.
    /// </para>
    /// <para>
    /// The whole thing is skipped when no API key is available, which keeps Stripe optional
    /// for contributors who are not working on payments.
    /// </para>
    /// </summary>
    /// <param name="builder">The distributed application builder.</param>
    /// <param name="target">The resource receiving the webhook signing secret, and the one the listeners wait for.</param>
    /// <param name="port">Port the target listens on — the listeners forward to it.</param>
    /// <param name="options">Optional overrides; the defaults suit a Next.js app with Stripe Connect.</param>
    public static IDistributedApplicationBuilder AddStripeListen<T>(
        this IDistributedApplicationBuilder builder,
        IResourceBuilder<T> target,
        int port,
        StripeListenOptions? options = null)
        where T : class, IResource, IResourceWithEnvironment
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(target);

        options ??= new StripeListenOptions();

        var apiKey = options.ApiKey ?? Environment.GetEnvironmentVariable("STRIPE_SECRET_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            Console.WriteLine("[Stripe] STRIPE_SECRET_KEY not set — skipping Stripe CLI listeners.");

            return builder;
        }

        var (cliPath, installNeeded) = StripeCli.ResolvePath(options);
        if (installNeeded && !options.InstallIfMissing)
        {
            // Adding the resources anyway would produce two red boxes and an unactionable
            // "$PATH" error. One sentence and no resources is the better failure.
            Console.WriteLine($"[Stripe] {StripeCli.InstallHint}");

            return builder;
        }

        // Set once before the target starts (see the BeforeStartEvent below) and read by the
        // environment callbacks, which run later, at resource start.
        var secret = new SecretHolder { Value = options.WebhookSecret
            ?? Environment.GetEnvironmentVariable("STRIPE_WEBHOOK_SECRET") };

        target.WithEnvironment(context =>
        {
            if (string.IsNullOrWhiteSpace(secret.Value)) return;

            context.EnvironmentVariables[options.WebhookSecretEnvironmentVariable] = secret.Value!;
            if (options.ConnectWebhookSecretEnvironmentVariable is { Length: > 0 } connectVar)
            {
                // Same value on purpose: one signing secret per API key covers both streams.
                context.EnvironmentVariables[connectVar] = secret.Value!;
            }
        });

        var standard = builder
            .AddExecutable(options.ResourceName, cliPath, ".", BuildArgs(options, port, connect: false))
            .WithEnvironment("STRIPE_API_KEY", apiKey)
            .WaitFor(target);

        IResourceBuilder<ExecutableResource>? connect = null;
        if (options.ConnectWebhookPath is { Length: > 0 })
        {
            connect = builder
                .AddExecutable(options.ConnectResourceName, cliPath, ".", BuildArgs(options, port, connect: true))
                .WithEnvironment("STRIPE_API_KEY", apiKey)
                .WaitFor(target);
        }

        builder.Eventing.Subscribe<BeforeStartEvent>(async (@event, ct) =>
        {
            var notifications = @event.Services.GetRequiredService<ResourceNotificationService>();

            if (installNeeded)
            {
                foreach (var resource in Resources(standard, connect))
                {
                    await notifications.PublishUpdateAsync(resource, s => s with
                    {
                        State = new ResourceStateSnapshot("Downloading Stripe CLI", KnownResourceStateStyles.Info),
                    }).ConfigureAwait(false);
                }

                try
                {
                    await StripeCli.EnsureInstalledAsync(cliPath, options.CliVersion, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // Surface the reason on the resources themselves; they will fail to start
                    // a moment later, and this is the line that says why.
                    foreach (var resource in Resources(standard, connect))
                    {
                        await notifications.PublishUpdateAsync(resource, s => s with
                        {
                            State = new ResourceStateSnapshot(
                                $"Stripe CLI download failed: {ex.Message}", KnownResourceStateStyles.Error),
                        }).ConfigureAwait(false);
                    }
                    Console.WriteLine($"[Stripe] {ex.Message} {StripeCli.InstallHint}");

                    return;
                }
            }

            if (!string.IsNullOrWhiteSpace(secret.Value)) return;

            secret.Value = await StripeCli.TryGetWebhookSecretAsync(cliPath, apiKey, ct).ConfigureAwait(false);
            if (secret.Value is null)
            {
                Console.WriteLine(
                    "[Stripe] WARNING: no webhook signing secret available — signature verification will fail. " +
                    "Set STRIPE_WEBHOOK_SECRET, or check that the API key is valid.");
            }
        });

        return builder;
    }

    private static IEnumerable<IResource> Resources(
        IResourceBuilder<ExecutableResource> standard,
        IResourceBuilder<ExecutableResource>? connect)
    {
        yield return standard.Resource;
        if (connect is not null) yield return connect.Resource;
    }

    private static string[] BuildArgs(StripeListenOptions options, int port, bool connect)
    {
        var path = connect ? options.ConnectWebhookPath! : options.WebhookPath;
        var url = $"http://{options.ForwardHost}:{port}{path}";

        var args = new List<string> { "listen", connect ? "--forward-connect-to" : "--forward-to", url };

        // Event filtering only makes sense on the standard stream; the Connect listener is
        // scoped by the account the events belong to, not by type.
        if (!connect && options.Events is { Length: > 0 } events)
        {
            args.Add("--events");
            args.Add(string.Join(',', events));
        }

        return [.. args];
    }

    /// <summary>Mutable box so the deferred environment callbacks see the captured secret.</summary>
    private sealed class SecretHolder
    {
        public string? Value { get; set; }
    }
}
