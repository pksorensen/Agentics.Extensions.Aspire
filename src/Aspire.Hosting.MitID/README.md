# Agentics.Extensions.Aspire.MitID

Wires the MitID test-user service (`agent-mitid`) into an AppHost, so integration
tests can approve MitID **pre-production** logins without a phone.

```csharp
builder.AddMitIdTestUsers(o => o.Project = "commuteconnects");

builder.AddProject<Projects.E2E>("e2e")
       .WithMitIdTestUsers();
```

The test then needs no configuration of its own:

```csharp
var mitid = MitIdApprover.FromEnvironment();          // Agentics.MitID.Testing
await mitid.ApproveAsync("commuteconnects", "Oscar39838", TimeSpan.FromSeconds(30));
```

## What it does, and what it deliberately does not

It adds **no container and starts no process.** agent-mitid keeps a registry of
named test users that has to outlive any one AppHost run — the whole point is
that `commuteconnects/Oscar39838` still exists tomorrow, on the build server, and
in someone else's checkout. So this is a reference to a running instance, not a
copy of one.

What it does is put `MITID_SERVICE_URL`, `MITID_MCP_TOKEN` and optionally
`MITID_PROJECT` on the resources that ask for them.

## When it is not configured

Nothing is injected, and that is on purpose: `MitIdApprover.FromEnvironment()`
then falls back to driving `pp.mitid.dk` directly, which needs no token and no
service. A laptop that has never been given a token still runs the suite; it just
names users by bruger-ID instead of through the registry.

Set `Optional = false` when a CI run must use the registry and a silent fallback
would hide a misconfiguration.

## Configuration

| Source | Key |
| --- | --- |
| Configuration / user secrets | `MitID:ServiceUrl`, `MitID:Token`, `MitID:Project` |
| Environment | `MITID_SERVICE_URL`, `MITID_MCP_TOKEN` |
| Code | `AddMitIdTestUsers(o => { o.ServiceUrl = …; o.Token = …; })` |

The token is the MCP bearer, read from the service PWA's **Indstillinger** tab.
Treat it as a password: it grants approval of every registered test login.

> **Pre-production only.** The identities involved are invented by MitID's own
> test-person generator and refer to nobody.
