# Agentics.Extensions.Aspire.Stripe

Runs the [Stripe CLI](https://docs.stripe.com/stripe-cli) as Aspire-managed webhook
listeners — **and installs it when it is missing**, instead of dying with
`exec: "stripe": executable file not found in $PATH`.

```bash
dotnet add package Agentics.Extensions.Aspire.Stripe
```

```csharp
var www = builder.AddJavaScriptApp("www", "../www").WithHttpEndpoint(port: wwwPort);

builder.AddStripeListen(www, wwwPort);
```

That one line adds two dashboard resources and sets `STRIPE_WEBHOOK_SECRET` +
`STRIPE_CONNECT_WEBHOOK_SECRET` on `www`.

## Why two listeners

`stripe listen` forwards **one** stream. A Connect integration has two, so the package adds
one resource per stream:

| Resource | Flag | Default path |
| --- | --- | --- |
| `stripe-webhooks` | `--forward-to` | `/api/webhooks/stripe` |
| `stripe-connect-webhooks` | `--forward-connect-to` | `/api/webhooks/stripe-connect` |

Both get the same signing secret: Stripe derives one `whsec_…` per API key, and it covers
both streams. Pass `ConnectWebhookPath = null` if you only need the standard one.

## How the CLI is found

In order, and none of it touches the network until the last step:

1. `StripeListenOptions.CliPath`
2. the `STRIPE_CLI_BIN` environment variable
3. a copy this package downloaded earlier (`~/.local/share/agentics/stripe-cli/<version>/`)
4. `stripe` on `PATH` — a CLI you installed yourself wins, so your own `stripe login` state
   and version keep applying
5. download the pinned release from Stripe's GitHub releases, verified against the sha256 in
   their published `stripe-*-checksums.txt`

Step 5 runs on `BeforeStartEvent` with a **Downloading Stripe CLI** state on both resources,
so a first run looks like a download rather than a hang. Set `InstallIfMissing = false` to
opt out — the listeners are then not added at all, and one line on the console tells you how
to install the CLI. Two red resources and a `$PATH` error is the outcome this package exists
to avoid.

## The signing secret

Resolved in this order:

1. `StripeListenOptions.WebhookSecret`
2. the `STRIPE_WEBHOOK_SECRET` environment variable
3. `stripe listen --print-secret`, run once before start

Because the secret is **deterministic per API key**, caching it in `apphost.run.json` after
the first run is a valid speed-up, not a correctness fix — step 3 will produce the same
value every time.

## The API key never enters the command line

The key is passed to the CLI as the `STRIPE_API_KEY` environment variable, not `--api-key`.
A live key in `argv` shows up in the Aspire dashboard's command display, in the console log
line for the resource, and in any `ps` on the machine. The environment variable does not.

Supply it via `StripeListenOptions.ApiKey`, or leave it to `STRIPE_SECRET_KEY`. **With no key
at all the listeners are skipped entirely** and the AppHost starts normally — Stripe stays
optional for people who are not working on payments.

## Options

| Option | Default | Notes |
| --- | --- | --- |
| `ApiKey` | `STRIPE_SECRET_KEY` env | No key ⇒ no listeners |
| `WebhookSecret` | `STRIPE_WEBHOOK_SECRET` env, else `--print-secret` | |
| `WebhookPath` | `/api/webhooks/stripe` | |
| `ConnectWebhookPath` | `/api/webhooks/stripe-connect` | `null` ⇒ no Connect listener |
| `WebhookSecretEnvironmentVariable` | `STRIPE_WEBHOOK_SECRET` | Set on the target resource |
| `ConnectWebhookSecretEnvironmentVariable` | `STRIPE_CONNECT_WEBHOOK_SECRET` | `null` ⇒ not set |
| `ResourceName` / `ConnectResourceName` | `stripe-webhooks` / `stripe-connect-webhooks` | Dashboard names |
| `Events` | all | `--events` filter, standard stream only |
| `ForwardHost` | `localhost` | |
| `CliPath` | `STRIPE_CLI_BIN` env | Skips the resolution ladder |
| `InstallIfMissing` | `true` | `false` ⇒ hard skip with install instructions |
| `CliVersion` | `1.50.5` | Pinned, so CI cannot silently change CLI version |

```csharp
builder.AddStripeListen(www, wwwPort, new StripeListenOptions
{
    WebhookPath = "/payments/stripe",
    ConnectWebhookPath = null,
    Events = ["checkout.session.completed", "customer.subscription.updated"],
});
```

## Relation to `CommunityToolkit.Aspire.Hosting.Stripe`

The CommunityToolkit package runs the CLI as a **container** (`docker.io/stripe/stripe-cli`)
and covers the standard stream only — it has no `--forward-connect-to`. This package runs the
CLI as a host process and covers both streams. Pick the toolkit one if you do not use Connect
and prefer a container; pick this one if you do use Connect, or if forwarding into a process
on the host (rather than a container reaching back out) is the simpler network.

## License

MIT
