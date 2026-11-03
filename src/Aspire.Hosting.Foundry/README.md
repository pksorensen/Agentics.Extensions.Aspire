# Agentics.Extensions.Aspire.Foundry

A local Azure AI Foundry / Azure OpenAI for Aspire AppHosts. One container answers:

| Surface | Routes |
| --- | --- |
| Inference (OpenAI v1) | `POST /openai/v1/responses` (JSON and streaming SSE), `GET /openai/v1/responses/{id}`, `POST /openai/v1/chat/completions`, `GET /openai/v1/models` |
| Inference (dated) | `POST /openai/deployments/{deployment}/chat/completions?api-version=…`, `POST /openai/responses?api-version=…` |
| ARM | `/arm/subscriptions/…/providers/Microsoft.CognitiveServices/accounts[/{a}[/deployments[/{d}]\|/listKeys\|/usages\|/models\|/projects]]`, `…/locations/{loc}/usages`, and `…/accounts/{a}/providers/Microsoft.Insights/metrics` |
| Emulator API | `/_emulator/usage`, `/_emulator/usage/records`, `/_emulator/usage/appmetrics`, `/_emulator/keys`, `/_emulator/scenarios`, `/_emulator/accounts`, `/healthz` |

Every inference route also works under `/{account}/…` and `/{account}/api/projects/{project}/…`,
the way `https://{resource}.openai.azure.com/openai/v1/` and
`https://{resource}.services.ai.azure.com/api/projects/{project}/openai/v1/` do. The
deployment is the `model` field. An account or deployment can carry **aliases**, so a
gateway path such as `/{business-line}/openai/v1` with a model named `gpt-5.1-eu`
lands on account `aif-demo`, deployment `gpt-5.1`.

## AppHost

```csharp
var tenant = builder.AddMicrosoftTenant(/* … */);

var foundry = builder.AddFoundryEmulator("foundry", o =>
    {
        o.HostPort = 5181;        // a fixed base URL for tools outside the AppHost (Codex)
        o.ChunkDelayMs = 15;      // stream at roughly model speed in a demo
    })
    .WithMicrosoftTenant(tenant)  // accept the tenant emulator's Entra bearer tokens
    .WithScenarios("scenarios")   // *.json / *.yaml, relative to the AppHost
    .WithAccount("aif-demo-swc", a =>
    {
        a.SubscriptionId = "d0e30000-0000-4000-8000-0000000000a1"; // fictional
        a.ResourceGroup = "rg-foundry-demo-swc";
        a.Aliases.Add("demo-line");                 // /{alias}/openai/v1
        a.WithDeployment("gpt-5.1", version: "2025-11-13", capacity: 500, aliases: "gpt-5.1-eu")
         .WithDeployment("gpt-5-mini", version: "2025-08-07")
         .WithKeys("local-dev-key-1");
    })
    // A gateway subscription key: what an APIM key is to the gateway in front of Foundry.
    .WithGatewayKey("acme-local-key", customer: "acme",
        subscriptionId: "acme-birgitte.holm", userPrincipal: "birgitte.holm@home-demo.test")
    // 30 days of deterministic history, so the usage page is not empty on first start.
    .WithSeededUsage("aif-demo-swc", "gpt-5.1", "acme", days: 30,
        dailyInputTokens: 4_000_000, dailyOutputTokens: 300_000,
        configure: u => { u.SubscriptionId = "acme-birgitte.holm"; u.CachedRatio = 0.5; });

builder.AddProject<Projects.Api>("api")
    .WithFoundryEmulator(foundry, "aif-demo-swc", deployment: "gpt-5.1");
```

`WithFoundryEmulator` sets `AZURE_OPENAI_ENDPOINT`, `AZURE_OPENAI_API_KEY`,
`OPENAI_BASE_URL` (the v1 base), `AZURE_OPENAI_DEPLOYMENT` and `FOUNDRY_ARM_ENDPOINT`
(the stand-in for `https://management.azure.com`; Azure SDK clients only use it if the
app passes it to e.g. `ArmClientOptions.Environment`).

While changing the emulator itself, build it from a checkout instead of pulling
`ghcr.io/pksorensen/foundry-emulator`:

```csharp
builder.AddFoundryEmulator("foundry")
    .WithLocalEmulatorSource("../../path/to/Agentics.Extensions.Aspire");
```

## Credentials

Checked in this order, all optional when `RequireAuth = false`:

1. `api-key` header — an account key (`key1`/`key2`, from `WithKeys` or derived from the
   account name) or a gateway key.
2. `Ocp-Apim-Subscription-Key` — a gateway key (or an account key).
3. `Authorization: Bearer …` — an account/gateway key, or an Entra token, which is
   checked against the tenant emulator's `tokeninfo` endpoint (`WithMicrosoftTenant`).
   The token's app id or user becomes the caller.

`sim-echo` and `sim-scenario:<name>` (the same keys the gateway LLM simulator uses) are
accepted as credentials that also pick a scenario. The header `x-foundry-sim:
scenario:<name>` does the same with any credential.

## Scenarios

A scenario is a list of steps; each step is one model turn — some text, some tool calls,
or a simulated error. Which step is served is derived from the conversation the client
sends, so it needs no server state and survives retries: every assistant message or tool
call already in the history moves it one step on.

```yaml
name: byg
description: "Codex: 'byg <noget>' scaffolds a tiny site in three turns."
trigger:
  promptRegex: '(?i)^\s*byg\s+(.+)$'   # matched against the first user message
steps:
  - text: "Jeg bygger **$prompt**. Først en mappe og en forside."
    calls:
      - shell: "mkdir -p $slug"
      - writeFile:
          path: "$slug/index.html"
          content: |
            <!doctype html><title>$prompt</title><h1>$prompt</h1>
    usage: { outputTokens: 420 }
  - text: "Nu en README."
    calls:
      - writeFile: { path: "$slug/README.md", content: "# $prompt\n" }
  - text: "Færdig. `$slug/` er klar."
```

Selection, first match wins: the caller's directive (`sim-scenario:` key or
`x-foundry-sim`) → a deployment's own `Scenario` → a scenario whose `trigger` matches
the first user message (and, if set, `trigger.deployments`) → a fallback reply that
echoes what was said.

Call kinds map onto whatever tools the client advertised:

| Call | Becomes |
| --- | --- |
| `shell: "cmd"` | `exec_command {cmd}` (Codex), `shell {command:[bash,-lc,cmd]}`, `shell_command` or `local_shell` |
| `writeFile: {path, content}` | `apply_patch` (*** Add File) when advertised, otherwise a heredoc through the shell tool |
| `applyPatch: "*** Begin Patch…"` | `apply_patch` (custom or function tool), otherwise `apply_patch <<'PATCH'` through the shell |
| `function: name`, `arguments: {…}` | that function call verbatim; `$toolMatching:<substr>` picks an advertised tool by name |

Substitutions in text and arguments: `$prompt` (the trigger's first capture group),
`$slug` (it, kebab-cased), `$firstUserText`, `$lastUserText`, `$model`, `$step`.
A step can instead be `error: {status: 429, message: …, retryAfterSeconds: 2}`; the
next request from the same session gets the step after it. `onExhausted` is
`fallback` (default), `last` or `error`.

Built-ins: `echo`, `shell-then-stop`, `rate-limit-then-succeed`. Scenarios can also be
uploaded at runtime: `PUT /_emulator/scenarios/{name}` with a JSON or YAML body.

Set `FOUNDRY_REQUEST_LOG_DIR` on the container to keep every request body, named by time,
conversation and the step that answered it (the newest 2000 are kept). It is the quickest
way to see exactly which tools and history an agent sends before writing a scenario.

## Codex

```toml
# $CODEX_HOME/config.toml
model = "gpt-5.1"
model_provider = "foundry-local"

[model_providers.foundry-local]
name = "Foundry emulator"
base_url = "http://localhost:5181/aif-demo-swc/openai/v1"
wire_api = "responses"
env_key = "FOUNDRY_LOCAL_KEY"     # an account or gateway key
```

Behind a gateway that wants an APIM subscription key, send it as a header instead:

```toml
[model_providers.acme]
base_url = "http://localhost:5181/demo-line/openai/v1"
wire_api = "responses"
env_http_headers = { "Ocp-Apim-Subscription-Key" = "FOUNDRY_KEY_ACME" }
```

`codex exec "byg en todo app"` then plays the `byg` scenario: Codex runs the scripted
commands itself (sandbox permitting — `-s workspace-write`, or
`--dangerously-bypass-approvals-and-sandbox` in a container without user namespaces) and
the usage lands on the key's customer.

## Usage

Every call is recorded with tokens (input, cached input, output, reasoning), account,
deployment, model, caller, customer, project, gateway subscription, user, initiative, the
route alias, the agent (`originator` header / user agent) and the scenario step. Live
records are appended to `usage.jsonl` in `FOUNDRY_DATA_DIR` (`PersistData = true` puts it
in a volume); seeded history is regenerated at each start and never stored.

Attribution, first wins: the key's binding → `x-foundry-customer` / `x-customer-id`,
`x-foundry-project` / `x-project-id`, `x-foundry-initiative` headers → `metadata.customer`,
`metadata.project`, `metadata.initiative` → the project in the route.

```
GET /_emulator/usage?groupBy=day,customer,model&from=2026-10-01&to=2026-10-29
GET /_emulator/usage?groupBy=day,subscription,user&customer=acme
GET /_emulator/usage/records?deployment=gpt-5.1
GET /_emulator/usage/appmetrics            # Log Analytics AppMetrics-shaped rows
DELETE /_emulator/usage                    # live records (?includeSeeded=true for all)
```

Rows use the field names a gateway usage endpoint typically returns: `day`, `customerId`,
`subscriptionId`, `userPrincipal`, `initiativeId`, `model`, `promptTokens`,
`cachedPromptTokens`, `completionTokens`, `reasoningTokens`, `totalTokens`, `requests`.
A bare date in `to` means through that day.

A new customer needs no restart:

```
POST /_emulator/keys {"customer":"nyt-demo-kunde","userPrincipal":"mads.lund@home-demo.test","initiative":"web"}
→ {"subscriptionId":"nyt-demo-kunde--web-mads.lund","primaryKey":"…"}
```

## ARM

The ARM router runs in this container, so management-plane calls go to
`{foundry}/arm/…`. Any dated api-version is accepted. Deployments report
`properties.model {format,name,version}`, `sku {name,capacity}`,
`provisioningState: Succeeded` and `capabilities {chatCompletion, responses: "true"}`;
`PUT`/`DELETE` on a deployment take effect for inference immediately. `listKeys`
returns `key1`/`key2`. `usages` reports `OpenAI.{sku}.{model}` with the summed
deployment capacity. Metrics (`InputTokens`, `OutputTokens`, `TotalTokens`,
`ProcessedPromptTokens`, `GeneratedTokens`, `cacheReadInputTokens`,
`AzureOpenAIRequests`) are computed from the usage log, honouring `timespan`,
`interval` and `$filter` on `ModelDeploymentName`, `ModelName` and `StatusCode`.

## Not emulated

Embeddings, images, audio, files, batches, fine-tuning, the Assistants / Agents API,
content filtering results, real tokenisation (counts are bytes/4), `encrypted_content`
for reasoning items, and the ARM write paths for accounts.
