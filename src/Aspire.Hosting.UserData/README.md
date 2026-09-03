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

## What it adds

Three commands on the resource:

| Command | What it does |
| --- | --- |
| **Fetch production data** | Streams the live data directory down over SSH, redacts it, marks it, and swaps it in — keeping the previous contents beside it. |
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

**Fetching is never a side effect.** Nothing is copied because someone ran the AppHost. The
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
aspire start -- --sync-prod
```

Fetches before any resource starts, so the application **boots** against production's data.

That is a different test from clicking the dashboard command, and the difference is the point.
Boot is a code path of its own: catch-up sweeps, queue drains, interrupted-run recovery and
start-up migrations all run once, at start, against whatever is already on disk. Fetching
afterwards can only ever produce that state after those have already run — so the one thing a
release does that nothing else does is exactly the thing the command cannot exercise. The switch
is how you find out that your nightly job's boot catch-up mails four months of backlog to real
people, before it does.

It always fetches; there is no freshness cache. "Give me production's state" that quietly hands
back yesterday's is the failure the switch exists to prevent — for a fast loop, restart without
it and keep the copy you have. A failure stops start-up rather than continuing against whatever
was there.

Because the fetch then happens once per restart, a backup chain would grow a pile of customer
data. So it does not: when the directory being replaced is itself a fetched snapshot, the
previous snapshot backup is deleted rather than stacked. Data somebody actually made — an empty
directory you filled by hand, a local test set — still gets the full chain.

## Finding the remote directory

`FromCoolify(fqdn, host)` asks the remote Docker daemon which container serves that hostname
(`COOLIFY_FQDN`) and reads the source of its `/app/user-data` mount. The container's name carries a
suffix that changes on every deploy; the FQDN does not, so this keeps working across releases.

`FromPath(host, remotePath)` when the box is not Coolify.

## Requirements

`ssh` and `tar` on the developer machine, and key-based access to the host — `BatchMode=yes` is
used, so a prompt is a failure rather than a hang. The transfer shells out to both rather than
using an SSH library on purpose: keys, agents, `known_hosts` and jump hosts are already solved by
the developer's own SSH configuration, and a library would solve all of it again, worse.

## Options

| Option | Default | Meaning |
| --- | --- | --- |
| `FromCoolify(fqdn, host)` / `FromPath(host, path)` | — | Where production's data lives. |
| `ContainerDataPath` | `/app/user-data` | Mount point inside the container, for the Coolify lookup. |
| `Exclude(...)` | none | Top-level entries not worth copying (analytics spools and the like). |
| `Redact(glob, mutate)` | — | **Required.** `*` matches within a path segment, `**` spans segments. |
| `RetainDays` | `7` | How long a copy and its backups survive. |
| `EnvironmentVariable` | `USER_DATA_DIR` | What the path is handed to the app as. |
| `LocalPathConfigurationKey` | `user-data-dir` | Configuration key a test run uses to override the directory. |
