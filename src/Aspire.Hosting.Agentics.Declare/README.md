# Agentics.Extensions.Aspire.Declare

An AppHost that needs a model endpoint, an API key or an app registration has, until someone hands
it one, two moves: stop and ask for a paste, or hope the value is already in user secrets. This adds
a third — **the composition declares what it needs, and a tool outside answers from what the
developer is already signed in to.** Nothing is typed, and nothing is written to disk.

```csharp
builder.AddAgenticsDeclare();

var baseUrl = builder.AddParameter("ai-base-url");
var apiKey  = builder.AddParameter("ai-api-key", secret: true);
var model   = builder.AddParameter("ai-model", new SuggestedValue("gpt-4o-mini"));

builder.AddAgenticsCapability("chat", "The model that answers on Overview")
       .Offers("foundry", "Azure AI Foundry — sign in once with `pks foundry init`")
       .Binds(baseUrl, "{endpoint:openai}")
       .Binds(apiKey,  "{apikey}")
       .Binds(model,   "{model:default}");
```

Then start it with [`pks aspire run`](https://github.com/pksorensen/pks-cli) instead of `aspire run`.

## What it adds

One pipeline step, `pks-declare`, with no dependencies and no resources behind it. `aspire do
pks-declare` builds the AppHost, walks the resource model and writes down which parameters this
composition needs — and, for the bound ones, which kind of credential would satisfy them. It starts
nothing: no container, no app, no port. Asking what a run needs must not be able to start the run.

`pks aspire run` runs that step first, resolves what it can on its own side, and starts the real
`aspire run` with the answers already in the environment as `Parameters__<name>`.

Parameters that are *not* bound are still reported, as things the tool knows it cannot fill — which
is the difference between "you have nothing configured" and "this run is going to stop and ask you".

## The placeholder vocabulary

| Placeholder | Resolves to |
| --- | --- |
| `{endpoint}` | the chosen provider's base URL |
| `{endpoint:openai}` | the same, shaped for an OpenAI client |
| `{apikey}` | a key for that endpoint, if the provider has one |
| `{model:<role>}` | the model the developer picked for a named role |
| `{imds:endpoint}` / `{imds:header}` | a loopback managed-identity proxy started for the run, and its per-run secret |
| `{entra:tenantid}` / `{entra:clientid}` / `{entra:clientsecret}` | an app registration provisioned under this capability's name — or `{entra:clientid:other-alias}` |

Anything else is passed through as a literal.

## The trap worth knowing

`aspire do` runs in **publish** mode. A composition that branches on `ExecutionContext.IsPublishMode`
— a Key Vault instead of parameters, a real tenant instead of an emulator — will describe the
*deployment* while the run that follows is a local one, and the parameters you wanted filled are
exactly the ones missing from the declaration. Nothing fails; the run just resolves nothing.

So fold the declare pass into the same condition:

```csharp
if (builder.ExecutionContext.IsPublishMode && !builder.IsAgenticsDeclaring())
{
    // actually publishing
}
```

## The dashboard reminder

An AppHost carrying this package that is started with plain `aspire run` puts a dismissible message
bar on the dashboard saying it runs best with pks. It stays away for a run started by pks, for a
publish, for the declare pass itself, and on any host with no dashboard client — which is what keeps
it out of CI and out of `Aspire.Hosting.Testing`. `PKS_ASPIRE_NO_REMINDER=1` silences it.

## Names

The API is `Agentics` and the wire is `pks`: `AddAgenticsDeclare` names *what you declare*, while
`pks-declare`, `PKS_DECLARE_OUT` and `PKS_ASPIRE_RUN` name *the tool that fills it*. Nothing in this
package references pks-cli or talks to it — an AppHost that carries it builds and runs unchanged on
a machine that has never heard of pks.

## Migrating from the copied file

Before 2026-08-29 this shipped as source: `pks aspire init` wrote `PksDeclare.cs` into the AppHost.
**Delete that file before adding this package** — otherwise `SuggestedValue` is defined twice and the
step is registered twice. Then rename the call sites: `AddPksDeclare` → `AddAgenticsDeclare`,
`AddPksCapability` → `AddAgenticsCapability`, `PksDeclareExtensions.IsDeclaring` →
`AgenticsDeclareExtensions.IsDeclaring`.
