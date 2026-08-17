# Agentics.Extensions.Aspire.FordConnect

An emulated **FordConnect 2.0** as one Aspire resource: the account-link door, the B2C
token endpoint, and the Query API — with Ford's measured rate limit and a car that
reports when it feels like it.

```csharp
var ford = builder.AddFordConnect(options =>
    {
        options.HostPort = 8090;   // pinned: the consent hop happens in a browser
    })
    .AddVehicle("EMULATEDMACHE00001", car =>
    {
        car.NickName = "Emuleret Mach-E";
        car.StateOfCharge = 62;
    })
    .AddOwner("owner@example.invalid", "Emuleret ejer");

builder.AddJavaScriptApp("server", "../server")
    .WithFordConnect(ford);   // the five FORD_* variables, pointed here
```

`WithFordConnect` is the whole integration. A client written against real Ford needs no
code change to run against this, because all three of Ford's hosts are configuration in
any client that got the auth flow right. If your client *cannot* be pointed here, that
is the finding.

## Why this exists

The real API cannot be developed against — three reasons, in the order they stop you:

1. `login.ford.com` is browser-only and times out from a devcontainer and from a server
   alike, so the consent hop cannot be completed from a dev box at all.
2. Every Query endpoint shares **one** rate-limit bucket with a burst of four or five and
   a refill of one call per fifteen seconds. One credential does not stretch across two
   developers and a test suite.
3. The data is a household's real car, including — because the log deliberately keeps it
   — where that car has been.

## What it reproduces, and what that cost to learn

Every default is a measurement taken against a 2022 Mustang Mach-E in August 2026. An
emulator milder than the thing it stands in for teaches nobody anything, so where the
measurement was harsh, the default is harsh.

- **One shared bucket.** Hammer `wallbox` to 429 and `telemetry`, `alerts` and `garage`
  answer 429 on their first call, untouched. This is the fact that decides
  architectures: a cadence per endpoint buys nothing, because the endpoints steal from
  each other.
- **No rate-limit headers at all.** Not `Retry-After`, not a remaining count — only a
  body with a message and a `traceId`. A client that reads a header here works locally
  and starves in production. `SendRateLimitHeaders = true` shows what a fair API would
  have told you.
- **A cold burst of five, refilling one per fifteen seconds, and it does not
  accumulate.** After pauses of 15 s, 30 s and 60 s there was exactly one call
  available each time, never four.
- **Twenty-minute access tokens**, not the hour most OAuth code is written against, and
  a refresh response that **omits `refresh_token`**. A client that overwrites blindly
  stores `undefined` and the link dies silently at the *next* refresh.
- **`auth/init`, not the B2C authorize endpoint.** Going straight to B2C fails with
  `AADB2C90075 RESTApiCallForUserInfo` before any user is involved, which reads like a
  broken credential and is not. `locale` is required — Ford answers
  `400 Invalid locale: _` without it.
- **A bare object for one car and an array for several** from `/garage`. A client that
  only handles the array shows an empty garage to every single-car account, which is
  most of them.
- **Sixty signals, seven of them lists.** `doorStatus`, `windowStatus`, `tirePressure`,
  `doorLockStatus`, `seatBeltStatus` and the two tyre-status signals come back as one
  entry per door or wheel with no top-level `value`. A renderer that assumes the
  `{value, updateTime}` envelope drops them silently.
- **`batteryStateOfCharge` is the 12 V battery.** It reads 64 % while the car is nearly
  full. The traction battery is `xevBatteryStateOfCharge`; both are emitted so the
  mistake is available to make locally.
- **A car that is not periodic.** Measured over three hours of one-minute polling: 204
  calls, 12 readings. Parked, it went quiet for 42 minutes; awake, it reported three
  times in five minutes, tightest gap 28 seconds. Polling faster does not make a Ford
  report faster, and a stub that answers fresh every time teaches the opposite.
- **The envelope's `updateTime` is not the newest stamp inside it.** It sat ahead of
  every metric in 8 of 12 readings, and in 1 of those 12 it had moved with no metric
  moving at all. Deduplicate on the metrics: keying on the envelope writes about one
  reading in twelve that the car never took — rare enough to survive review.

## The portal

Opening the resource endpoint gives a control surface, because the behaviour worth
developing against cannot be reached by waiting. Turn the ignition on, drive 10 km, plug
in, start charging, hand the bucket its burst back, or withdraw consent — and the next
read answers accordingly. It also shows the shared bucket, which is otherwise invisible.

The consent page lives here too: pick an owner, then **Tillad** or **Afvis**. A refusal
comes back on the redirect as `error=access_denied`, not as an error page — the branch
most clients forget.

## Replay: the afternoon that actually happened

`Simulate` (the default) reproduces Ford's behaviour. `Replay` reproduces a specific
recording — the exact payloads, in the gaps they arrived in.

```csharp
var ford = builder.AddFordConnect(o => o.HostPort = 8090)
    .WithReplayLog(".local/ford-export.jsonl", speed: 60);
```

The input is a JSONL event log: one object per line, and the `ford.reading` rows are the
ones read. Those rows exist because the Query API has no history endpoint — an hour not
logged is an hour gone — and having written them down, replaying them is nearly free.

- The recorded payload is served **byte for byte**, envelope included. A replay that
  freshened timestamps would be a simulator with extra steps.
- Time is **scaled, not resampled**: `speed: 60` turns an hour of log into a minute, and
  a 42-minute silence into 42 seconds of the same reading. Polling inside a silence
  returns the same payload again, which is exactly what a cadence experiment must meet.
- The end **holds**; it does not loop. Looping would run the odometer backwards, and no
  car does that.
- The VINs come from the log and replace the seeded vehicles, so the one-car/many-car
  garage shape stays honest.
- Replay costs rate-limit tokens like everything else. A recording that was not also
  rate limited would give a cadence experiment the wrong answer.

**Never commit an export.** A real one carries the full VIN and, since the log keeps
coordinates, the owner's home address. Keep it in a git-ignored directory, or hand-write
a fixture with a fake VIN.

## Notes

- `HostPort` is worth pinning whenever the consent flow matters: the authorize hop leaves
  from a browser and comes back to the app's own callback, so both ends must be addresses
  a browser used — which an Aspire-proxied endpoint on a moving port is not.
- The credential is local-only by construction. The emulator accepts exactly its own
  seeded client id and secret, so a real Ford secret in an AppHost would be both a leak
  and pointless.
- This is a development emulator, not a Ford conformance suite. Where it and the real API
  disagree, the real API is right — and the disagreement is worth writing down here,
  because everything in this list was once a surprise.
