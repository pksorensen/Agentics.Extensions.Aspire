namespace Aspire.Hosting.Stripe;

/// <summary>
/// Shape of the Stripe CLI listeners added by
/// <see cref="StripeExtensions.AddStripeListen{T}(IDistributedApplicationBuilder, ApplicationModel.IResourceBuilder{T}, int, StripeListenOptions?)"/>.
/// Every default matches what a Next.js app with Stripe Connect needs, so the common case
/// passes no options at all.
/// </summary>
public sealed class StripeListenOptions
{
    /// <summary>
    /// Stripe secret key. Defaults to the <c>STRIPE_SECRET_KEY</c> environment variable.
    /// When neither is set the listeners are skipped entirely — Stripe stays optional.
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// Webhook signing secret (<c>whsec_…</c>). Defaults to the <c>STRIPE_WEBHOOK_SECRET</c>
    /// environment variable; when that is empty it is captured from the CLI with
    /// <c>stripe listen --print-secret</c>. The value is deterministic per API key, so
    /// caching it in apphost.run.json is a valid optimisation, not a correctness fix.
    /// </summary>
    public string? WebhookSecret { get; set; }

    /// <summary>Path the standard listener forwards to. Default <c>/api/webhooks/stripe</c>.</summary>
    public string WebhookPath { get; set; } = "/api/webhooks/stripe";

    /// <summary>
    /// Path the Connect listener forwards to (<c>--forward-connect-to</c>). Default
    /// <c>/api/webhooks/stripe-connect</c>. Set to <see langword="null"/> to add no Connect
    /// listener at all.
    /// </summary>
    public string? ConnectWebhookPath { get; set; } = "/api/webhooks/stripe-connect";

    /// <summary>Environment variable the target resource receives the signing secret in.</summary>
    public string WebhookSecretEnvironmentVariable { get; set; } = "STRIPE_WEBHOOK_SECRET";

    /// <summary>
    /// Environment variable the target resource receives the Connect signing secret in.
    /// It is the same value: one <c>whsec_</c> per API key covers both streams.
    /// Set to <see langword="null"/> to leave it unset.
    /// </summary>
    public string? ConnectWebhookSecretEnvironmentVariable { get; set; } = "STRIPE_CONNECT_WEBHOOK_SECRET";

    /// <summary>Dashboard name of the standard listener.</summary>
    public string ResourceName { get; set; } = "stripe-webhooks";

    /// <summary>Dashboard name of the Connect listener.</summary>
    public string ConnectResourceName { get; set; } = "stripe-connect-webhooks";

    /// <summary>
    /// Restrict the standard listener to these event types (<c>--events</c>). Null forwards
    /// everything, which is what the Stripe CLI does by default.
    /// </summary>
    public string[]? Events { get; set; }

    /// <summary>Host the listeners forward to. Default <c>localhost</c>.</summary>
    public string ForwardHost { get; set; } = "localhost";

    /// <summary>
    /// Explicit path to the <c>stripe</c> binary. Skips the whole resolution ladder.
    /// Falls back to the <c>STRIPE_CLI_BIN</c> environment variable.
    /// </summary>
    public string? CliPath { get; set; }

    /// <summary>
    /// Download the CLI from Stripe's GitHub releases (checksum-verified) when it is not
    /// already installed. Turn this off to get a hard error with install instructions
    /// instead — e.g. on a build agent that must not reach the network.
    /// </summary>
    public bool InstallIfMissing { get; set; } = true;

    /// <summary>
    /// Version downloaded when <see cref="InstallIfMissing"/> applies. Pinned rather than
    /// "latest" so a CI run cannot silently change which CLI it exercises.
    /// </summary>
    public string CliVersion { get; set; } = "1.50.5";
}
