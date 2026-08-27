# Agentics.MitID.Testing

Approve a MitID login from a test. No phone, no browser, about a second.

MitID's **pre-production** environment lets a code app be played over plain
REST — every value posted comes out of a previous response, and the test backend
signs on the app's behalf. So the moment your Playwright test reaches *"Åbn MitID
app og godkend"*, one call finishes the login:

```csharp
var mitid = MitIdApprover.FromEnvironment();

await page.ClickAsync("#approve-on-another-device-button");   // dispatches the transaction
var approval = await mitid.ApproveAsync("commuteconnects", "Oscar39838", TimeSpan.FromSeconds(30));

Console.WriteLine(approval.Reference);   // Commute Connects (TEST): Log på hos Commute Connects (TEST)
```

> **Pre-production only.** The identities are invented by MitID's own test-person
> generator and refer to nobody. The host is a constant, not a setting — this
> library cannot be pointed at production MitID, by design.

## Two ways to name a user

| | `MitIdApprover.Direct()` | `MitIdApprover.ViaService(url, token)` |
| --- | --- | --- |
| Talks to | `pp.mitid.dk` | an `agent-mitid` instance |
| Needs | nothing — MitID hands its client credentials to any caller | a bearer token, and the service up |
| Names a user by | bruger-ID | project + bruger-ID, resolved through a registry |
| PIN | the Test Tool default, or yours | whatever the registry holds |
| Survives someone recreating the user | no | yes |

`FromEnvironment()` uses the service when `MITID_SERVICE_URL` and
`MITID_MCP_TOKEN` are set and falls back to direct when they are not — the shared
registry in CI, a working test on a laptop with no token.

## The one thing that breaks tests

**"Åbn app på anden enhed" is a choice, not a wait.** Nothing is dispatched to
any app until that button is pressed, and on the MitID login page it lives inside
an `about:blank` iframe:

```csharp
foreach (var frame in page.Frames)
{
    if (await frame.QuerySelectorAsync("#approve-on-another-device-button") is not null)
    {
        await frame.ClickAsync("#approve-on-another-device-button");
        break;
    }
}
```

Approve called before that click finds nothing pending, no matter how long it
polls. (The user-ID input has the same trap in reverse: nine of the ten inputs on
that page are hidden decoys, so select `input.mitid-core-user__user-id:visible`.)

## Refusal, and unattended approval

`RejectAsync` is the same protocol call with one bit flipped, so a test can check
what the app under test does when the user says no — the relying party gets an
OAuth `error`, not a `code`.

`AutoApproveAsync(project, userId, window)` opens a time-boxed window in which the
service answers by itself. Reach for it only when the login is triggered somewhere
the test cannot call approve — a browser someone is clicking by hand. When the
test drives the login, `ApproveAsync` with a wait is better: it is exact, it arms
nothing, and it puts the reference text in your own assertions.

## What this is not

Nothing here creates a session in *your* application. It plays the MitID app;
your app still goes through its own broker and its own OIDC exchange. And it is
not a MitID client library — there is no production surface in it at all.
