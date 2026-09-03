# Agentics.Extensions.Aspire.Registry

`UseAgenticsRegistry()` — check, before startup, whether this machine can
actually pull the private container images the AppHost uses, and put the
sign-in steps at the top of the dashboard when it cannot.

## The failure it removes

A resource whose image lives on `registry.agentics.dk` fails several seconds
into startup with:

```
error from registry: unauthorized
```

That line lands in one resource's console log, where it reads like the resource
is broken. It isn't — the machine is not signed in, and every other resource
from that registry is about to fail the same way.

## Usage

```csharp
var builder = DistributedApplication.CreateBuilder(args);

builder.UseAgenticsRegistry();
```

That is the whole integration. It walks the app model for container images on
`registry.agentics.dk`, and if there are any, probes one of them in the
background. Nothing waits on it.

When the probe comes back **unauthorized**, a warning appears in the dashboard's
message bar naming the resources that will fail, with a *Show me how* button
that opens the sign-in steps.

Add a per-resource retry button so a failed resource can recover without
restarting the AppHost:

```csharp
var doorman = builder.AddDoorman("doorman")
                     .WithRegistryAccessCheck();
```

Pressing **Check registry access** re-probes and starts the resource when the
answer has changed.

## Options

```csharp
builder.UseAgenticsRegistry(options =>
{
    options.Registry = "registry.agentics.dk";   // images from anywhere else are ignored
    options.AdditionalImages.Add("registry.agentics.dk/agentics/pks-agent-git:latest");
    options.Timeout = TimeSpan.FromSeconds(20);
    options.ContainerRuntime = "docker";
    options.SignInCommand = "agent-registry login";
    options.Notify = true;                        // false: log only, no dashboard banner
});
```

`AdditionalImages` is for images the AppHost pulls without declaring them as
resources — a build script's base image, for instance.

## How the check works, and what it will not claim

The probe is `docker manifest inspect <image>`. That exercises docker's real
credential chain — `config.json`, `credHelpers`, the credential store — and
stops at the manifest, so it costs one request and downloads no layers. Reading
a config file instead would answer a different and less useful question: what
decides whether the pull works is docker's own resolution order, not what a
file looks like.

Only *unauthorized* raises the banner. A missing daemon, a dropped network, a
timeout, or an image that simply does not exist are all reported as unknown and
stay in the AppHost log at debug level. An AppHost that tells someone to sign in
because their wifi went down is worse than one that says nothing.

## Signing in

Docker has no OAuth for third-party registries — `docker login` takes a
username and a password and nothing else. The browser flow therefore lives in
the registry's own CLI, which then answers Docker's credential-helper protocol
on the user's behalf:

```bash
agent-registry login
```

See [pks-agent-registry](https://github.com/pksorensen/pks-agent-registry) —
ADR 0004 for the design.
