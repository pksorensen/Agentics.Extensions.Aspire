# Agentics.Extensions.Aspire.UserData

For apps that keep their state in a directory rather than a database — `USER_DATA_DIR` and a
file store. Points a resource at that directory, and puts *"fetch a fresh copy of production"*
behind a button in the Aspire dashboard, with the secrets stripped out on the way in.

```csharp
var www = builder.AddJavaScriptApp("www", "../www");

www.WithUserData("../www/user-data", data => data
    .FromCoolify("carshare.example.com", host: "root@10.0.0.1")
    .Exclude("visitors")
    .Redact("logins/*/credentials.json", json =>
    {
        json.Remove("passwordHash");
        json["resetPasswordOnLogin"] = true;
    }));
```

Azure Container Apps with an Azure Files volume authenticate the developer directly with Microsoft
Entra ID and never download a storage account key. ARM discovery, PIM activation and file transfer
do not depend on Azure CLI:

```csharp
www.WithUserData("../www/user-data", data => data
    .FromAzureContainerApp(
        subscriptionId: "00000000-0000-0000-0000-000000000000",
        resourceGroup: "rg-example-prod-weu",
        containerAppName: "web",
        containerDataPath: "/data")
    .AuthenticateAzureUser("00000000-0000-0000-0000-000000000000")
    .ActivateEligibleAzureRoles(
        "acdd72a7-3385-48ef-bd42-f606fba81ae7", // Reader
        "b8eda974-7b85-4f76-af95-65846b26df6d") // Storage File Data Privileged Reader
    .Exclude("workflow")
    .Redact("owners/*/users/*.json", json => json.Remove("subject")));
```

## What it adds

Three commands on the resource:

| Command | What it does |
| --- | --- |
| **Fetch production data** | Downloads the live data directory, redacts it, marks it, and swaps it in — keeping the previous contents beside it. |
| **Reset to empty** | Starts over with an empty directory, keeping the current one as a backup. |
| **Restore previous data** | Puts the most recent backup back. |

…and sets `USER_DATA_DIR` (or whatever `EnvironmentVariable` you name) on the resource.

## The four rails

Copying production down is genuinely useful — a bug that only exists in real data is otherwise
unreachable. It is also the easiest way to spread a customer's credentials across laptops. The
package is built so the safe version is the only version available.

**Redaction is required.** `WithUserData` throws if you have not named at least one thing to
strip. There is no default, because only the application knows where it keeps password hashes and
tokens — and a default of "copy it all, opt in later" would mean every app that adopted this
shipped production's secrets to a dev machine on day one. A file that matches a redaction rule but
does not parse as JSON is deleted rather than passed through.

**Fetching is never an implicit side effect.** Nothing is copied because someone ran the AppHost. The
dashboard command shows a confirmation naming the host and the retention period first, and the
one way to fetch at start-up — `--sync-prod`, below — is read from the process command line and
from nowhere else. Configuration, environment variables and launch profiles are all excluded on
purpose: a switch that can be parked in `apphost.run.json` is a switch that copies a customer's
data onto every machine that ever runs the solution. It also refuses to run when the data
directory has been overridden for test isolation.

**Every copy is marked.** `.snapshot-origin.json` lands in the directory with `source: "prod"`,
the host, the time, and what was excluded and redacted. Enforcement belongs to the app: read the
marker and refuse to do anything that reaches real people. The obvious one is mail — a nightly
job or a debugging click will otherwise write to real customers, and most mail modules default to
a real provider when the environment variable is unset.

**Copies expire.** Fetched data and its backups are deleted after `RetainDays` (7 by default).
Retention is the difference between a debugging aid and an unmanaged second copy of the customer
database.

## Rehearsing a release: `--sync-prod`

```bash
aspire run --sync-prod developer@example.com
# Equivalent detached/lifecycle form:
aspire start -- --sync-prod developer@example.com

# When user authentication is blocked, use an application client id. Aspire asks for the
# client secret in a masked dashboard dialog and keeps it in memory only for this run:
aspire run --sync-prod 00000000-0000-0000-0000-000000000000
```

The Aspire dashboard starts immediately and shows a dedicated `*-sync-prod` resource while the
copy runs. Application resources wait for that resource to finish successfully, so the
application still **boots** against production's data without making dashboard startup wait for
the transfer.

That is a different test from clicking the dashboard command, and the difference is the point.
Boot is a code path of its own: catch-up sweeps, queue drains, interrupted-run recovery and
start-up migrations all run once, at start, against whatever is already on disk. Fetching
afterwards can only ever produce that state after those have already run — so the one thing a
release does that nothing else does is exactly the thing the command cannot exercise. The switch
is how you find out that your nightly job's boot catch-up mails four months of backlog to real
people, before it does.

It always checks production; it does not use a time-based freshness cache. The first run downloads
a complete indexed snapshot. Later runs compare Azure's file version metadata with the manifest
in `.snapshot-origin.json`, verify the local SHA-256 hashes, and transfer only new, changed,
missing, or locally modified files. Files deleted in production disappear from the new local
snapshot. Unchanged files are assembled locally before the same atomic directory swap, so a
failure still leaves the prior snapshot intact. A failure leaves dependent application resources
stopped; the sync resource shows the error in the dashboard.

Because the fetch then happens once per restart, a backup chain would grow a pile of customer
data. So it does not: when the directory being replaced is itself a fetched snapshot, the
previous snapshot backup is deleted rather than stacked. Data somebody actually made — an empty
directory you filled by hand, a local test set — still gets the full chain.

## Finding the remote directory

`FromCoolify(fqdn, host)` asks the remote Docker daemon which container serves that hostname
(`COOLIFY_FQDN`) and reads the source of its `/app/user-data` mount. The container's name carries a
suffix that changes on every deploy; the FQDN does not, so this keeps working across releases.

`FromPath(host, remotePath)` when the box is not Coolify.

`FromAzureContainerApp(subscriptionId, resourceGroup, containerAppName, containerDataPath)` finds
the Azure Files volume mounted at that path and downloads it through the Azure Files SDK.
`AuthenticateAzureUser(tenantId)` opens an interactive browser sign-in on the first run, caches the
selected account independently of Azure CLI, and silently reuses it on later runs. Supplying the user after
`--sync-prod` both selects and verifies the account. The signed-in developer needs Reader access to
the Container App and managed environment, plus **Storage File Data Privileged Reader** on the
backing storage account/share.
The `--backup-intent` request bypasses file ACLs using that read-only data-plane role; storage
keys are neither queried nor printed.

Supplying an application client id instead of a user principal name after `--sync-prod` uses the OAuth
client-credentials flow. The Aspire dashboard asks for the client secret in a masked field; the secret is
not written to configuration, the command line, logs or the token cache. The application's service
principal needs permanent **Reader** on the production resource group and **Storage File Data Privileged
Reader** on the backing storage account. Application identities cannot activate a user's eligible PIM roles.

## Reusing the support identity locally

An AppHost may deliberately share the interactive user credential with a local loopback gateway:

```csharp
var gateway = new AzureUserAccessProxy(
    emulatorUserInfoEndpoint,
    [new("/openai/", foundryOpenAiBaseUrl, "https://cognitiveservices.azure.com/.default")]);

data.AuthenticateAzureUser(tenantId)
    .ShareAzureUserWith(gateway);
```

This keeps the application on its ordinary local OIDC setup. It sends a bearer token issued by the
local Microsoft tenant emulator to the gateway; this can be the signed-in local user token or a
client-credentials token for the application's emulated app registration. The gateway validates the
token against the emulator, obtains an audience-specific Azure token from the user credential held in
the AppHost process, and forwards the call. Azure tokens are never returned to the application,
browser, data snapshot or logs.

The gateway listens only on `127.0.0.1`, rejects requests until an explicit sync has authenticated the
Azure user, accepts only configured slash-delimited routes, refuses path traversal, replaces rather than
forwards `Authorization`, and does not forward cookies or arbitrary request headers. Upstream redirects
are disabled. This is a local support/development bridge, not a deployment authentication mechanism.

## Requirements

For SSH sources: `ssh` and `tar` on the developer machine, and key-based access to the host —
`BatchMode=yes` is used, so a prompt is a failure rather than a hang. The transfer shells out to
both rather than using an SSH library on purpose: keys, agents, `known_hosts` and jump hosts are
already solved by the developer's own SSH configuration, and a library would solve all of it
again, worse.

For Azure sources: a browser for the first interactive sign-in and the read-only roles described
above. In a remote Linux devcontainer the Aspire dashboard shows the captured Microsoft authorize
URL as a notification link; the browser returns to the loopback listener started by Azure Identity.
Azure CLI is not required.

When those roles are eligible through PIM rather than permanently active,
`ActivateEligibleAzureRoles(...)` makes an explicit fetch self-activate them for one hour after
the first `AuthorizationFailed`, wait for RBAC propagation, and continue. The signed-in user must
already be eligible at the configured resource-group scope. Approval and MFA requirements are not
bypassed: the fetch stops with instructions to complete them in PIM.

## Options

| Option | Default | Meaning |
| --- | --- | --- |
| `FromCoolify(...)` / `FromPath(...)` / `FromAzureContainerApp(...)` | — | Where production's data lives. |
| `AuthenticateAzureUser(tenantId, publicClientId?)` | — | Direct Entra user authentication with a persistent local account/token cache. |
| `ShareAzureUserWith(proxy)` | none | Gives a loopback-only allowlisted proxy the identity authenticated by an explicit sync. |
| `ContainerDataPath` | `/app/user-data` | Mount point inside the container, for the Coolify lookup. |
| `Exclude(...)` | none | Top-level entries not worth copying (analytics spools and the like). |
| `Redact(glob, mutate)` | — | **Required.** `*` matches within a path segment, `**` spans segments. |
| `RetainDays` | `7` | How long a copy and its backups survive. |
| `MaxConcurrentAzureDownloads` | `8` | Parallel Azure Files SDK downloads used for new and changed files. |
| `EnvironmentVariable` | `USER_DATA_DIR` | What the path is handed to the app as. |
| `LocalPathConfigurationKey` | `user-data-dir` | Configuration key a test run uses to override the directory. |
