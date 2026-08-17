using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.Concurrent;
using System.Text.Json;

namespace Aspire.Hosting.FordConnect.Emulator;

// An emulated FordConnect 2.0. Everything in here is a measurement written down as
// code; the comments say which, because a default nobody can trace is a default
// somebody will "fix".
//
// The three URL prefixes are Ford's, not ours. They all sit on one hostname in
// production and it is easy to assume they are one service; they are not, and a
// client that hard-codes any of them cannot be pointed at this emulator. Keeping
// the same shapes here is what makes the swap a matter of configuration.

/// <summary>One call against the emulator, for the portal's activity list.</summary>
public sealed record FordConnectCall(
    DateTimeOffset At,
    string Endpoint,
    int Status,
    int TokensLeft,
    string? Note);

/// <summary>
/// The shared rate-limit bucket.
///
/// Measured 2026-08-16 against the real API, and every number here is one of those
/// measurements:
///
///   - ONE bucket for every Query endpoint. Hammering <c>wallbox</c> to 429 also 429s
///     <c>telemetry</c>, <c>alerts</c> and <c>garage</c> on their first call. This is
///     the fact that decides architectures, because the intuitive design — a cadence
///     per endpoint — buys nothing: the endpoints steal from each other.
///   - A cold burst of four to five calls, not ten.
///   - Refill about one call per fifteen seconds, and it does NOT accumulate: after
///     pauses of 15 s, 30 s and 60 s there was exactly one call available each time.
///
/// The cold burst is modelled as one-time. That is the strict reading of the
/// measurement — we never saw it come back within a minute, and we did earn a 429 six
/// quiet minutes after a restart, which hints at a longer window we never established.
/// An emulator should be at least as harsh as the thing it stands in for, so this
/// errs that way; <see cref="FordConnectOptions.RateLimitAccumulates"/> relaxes it.
/// </summary>
public sealed class FordConnectBucket(FordConnectBehaviour behaviour, Func<DateTimeOffset> now)
{
    private readonly object _gate = new();
    private int _burstLeft = behaviour.RateLimitBurst;
    private DateTimeOffset _lastAllowed = DateTimeOffset.MinValue;

    public int TokensLeft
    {
        get { lock (_gate) return _burstLeft > 0 ? _burstLeft : Available() ? 1 : 0; }
    }

    /// <summary>Milliseconds until the next call would be allowed. Not sent to any
    /// client — Ford sends no <c>Retry-After</c> — but the portal shows it, because a
    /// developer staring at a 429 deserves to know.</summary>
    public int WaitMs
    {
        get
        {
            lock (_gate)
            {
                if (_burstLeft > 0 || Available()) return 0;
                var elapsed = (now() - _lastAllowed).TotalMilliseconds;
                return (int)Math.Max(0, behaviour.RateLimitRefillMs - elapsed);
            }
        }
    }

    public bool TryTake()
    {
        lock (_gate)
        {
            if (behaviour.RateLimitAccumulates)
            {
                // Clamped in floating point BEFORE the cast, and the reason is a bug this
                // had: `_lastAllowed` starts at DateTimeOffset.MinValue, so on the first
                // call the elapsed time is two thousand years. Divided by the refill that
                // is ~6e11, an unchecked (int) cast of it wrapped to a large positive
                // number, `_burstLeft + earned` overflowed to negative, and the bucket
                // refused the very first request of the process while reporting 60 tokens
                // left. It only bit with RateLimitAccumulates on — which is to say, only
                // for whoever flipped the flag to see what a fair bucket felt like.
                var earned = (int)Math.Clamp(
                    (now() - _lastAllowed).TotalMilliseconds / behaviour.RateLimitRefillMs,
                    0,
                    behaviour.RateLimitBurst);
                if (earned > 0)
                {
                    _burstLeft = Math.Min(behaviour.RateLimitBurst, _burstLeft + earned);
                    _lastAllowed = now();
                }
            }

            if (_burstLeft > 0)
            {
                _burstLeft--;
                _lastAllowed = now();
                return true;
            }

            if (!Available()) return false;
            _lastAllowed = now();
            return true;
        }
    }

    private bool Available() => (now() - _lastAllowed).TotalMilliseconds >= behaviour.RateLimitRefillMs;

    /// <summary>Hands the cold burst back. Only the portal calls this — there is no
    /// endpoint for it, because a rate limit an app can reset is not a rate limit.</summary>
    public void Refill()
    {
        lock (_gate)
        {
            _burstLeft = behaviour.RateLimitBurst;
            _lastAllowed = DateTimeOffset.MinValue;
        }
    }
}

/// <summary>
/// The simulated car.
///
/// The important thing it reproduces is not the values but the *reporting*: a Ford
/// does not report on an interval. Measured 2026-08-17 over three hours of one-minute
/// polling — 191 calls, 7 distinct readings — a parked car went quiet for 42 minutes,
/// then while awake reported three times inside five minutes, tightest gap 28 seconds.
///
/// Anything that tunes a polling cadence has to meet that shape, because the naive
/// conclusion from a stub that answers with fresh data every time is "poll faster and
/// get more", and against the real car that is false at any price.
/// </summary>
public sealed class FordConnectCar
{
    private readonly object _gate = new();
    private readonly Func<DateTimeOffset> _now;
    private readonly Random _random;

    /// <summary>Signal name → when the car last reported that signal. Per signal, not
    /// per read: Ford's own timestamps sit on the metrics, and their ages differ by
    /// weeks. A live payload held one from a month earlier next to one from ten
    /// minutes earlier.</summary>
    private readonly Dictionary<string, DateTimeOffset> _stamps = new(StringComparer.Ordinal);

    private DateTimeOffset _nextReport;

    /// <summary>The envelope's own <c>updateTime</c> — the record's stamp, not the
    /// newest signal's. See <see cref="FordConnectTelemetry"/> for the measurement that
    /// says these are different things.</summary>
    private DateTimeOffset _envelope;

    public FordConnectCar(FordConnectVehicle seed, Func<DateTimeOffset> now)
    {
        _now = now;
        // Seeded from the VIN so a restart replays the same car. A test that fails
        // only sometimes is a test nobody trusts.
        _random = new Random(seed.Vin.GetHashCode(StringComparison.Ordinal));

        Vin = seed.Vin;
        Seed = seed;
        StateOfCharge = seed.StateOfCharge;
        Odometer = seed.Odometer;
        Latitude = seed.Latitude;
        Longitude = seed.Longitude;
        VehicleId = Guid.NewGuid().ToString();

        var start = now();
        // Not all at once. Some signals are genuinely ancient in a real payload —
        // `configurations` and `oilLifeRemaining` were weeks old — and a client that
        // assumes one read means one timestamp should break here rather than in
        // production.
        foreach (var name in FordConnectTelemetry.SignalNames)
            _stamps[name] = start - TimeSpan.FromMinutes(_random.Next(1, 90));
        foreach (var name in FordConnectTelemetry.SlowSignals)
            _stamps[name] = start - TimeSpan.FromDays(_random.Next(3, 30));

        _nextReport = start;
        _envelope = start;
    }

    public string Vin { get; }
    public string VehicleId { get; }
    public FordConnectVehicle Seed { get; }

    public string Ignition { get; private set; } = "OFF";
    public string Plug { get; private set; } = "DISCONNECTED";
    public string ChargeStatus { get; private set; } = "NotReady";
    public double StateOfCharge { get; private set; }
    public double Odometer { get; private set; }
    public double Latitude { get; private set; }
    public double Longitude { get; private set; }
    public double Speed { get; private set; }
    public double HeadingDegrees { get; private set; } = 90;

    public bool Awake => Ignition == "ON" || ChargeStatus is "ChargingAC" or "ChargingDC";
    public DateTimeOffset NextReportAt { get { lock (_gate) return _nextReport; } }
    public DateTimeOffset EnvelopeStamp { get { lock (_gate) return _envelope; } }

    public IReadOnlyDictionary<string, DateTimeOffset> Stamps
    {
        get { lock (_gate) return new Dictionary<string, DateTimeOffset>(_stamps); }
    }

    /// <summary>
    /// Advance the world, then report if the car feels like it.
    ///
    /// Called on every read rather than on a timer, so the emulator has no background
    /// thread and a test can drive it purely by asking. Whether anything *changes* is
    /// the car's business, which is the whole point.
    /// </summary>
    public void Tick()
    {
        lock (_gate)
        {
            var now = _now();
            if (now < _nextReport)
            {
                // The read that learns nothing — 192 of the 204 measured. Ford's record
                // still moves under it now and then: measured once in twelve readings,
                // which over 204 reads is about one read in two hundred. Reproduced at
                // that rarity on purpose, because a client deduplicating on the envelope
                // has to be wrong here *occasionally* to be caught the way production
                // catches it, and wrong on every read to be caught by a stub.
                if (_random.Next(200) == 0) _envelope = now;
                return;
            }

            // The metrics a report carries were sampled before the record was written.
            // That is why the envelope sat ahead of every signal inside it in 8 of 12
            // measured readings — not because it is a different clock, because it is a
            // later moment.
            var sampled = now - TimeSpan.FromSeconds(_random.Next(3, 90));

            if (Ignition == "ON")
            {
                // A report's worth of driving. 50 km/h is a plausible average and the
                // arithmetic below is deliberately crude — a route planner would be a
                // product, and this is a fixture.
                var hours = Math.Max(0.0, (now - _stamps[FordConnectTelemetry.Odometer]).TotalHours);
                var km = Math.Min(5.0, hours * 50.0);
                Odometer += km;
                StateOfCharge = Math.Max(0, StateOfCharge - km * 0.25);
                Speed = km > 0 ? 50 : 0;
                // ~111 km per degree of latitude. Good enough to make a track that
                // looks like a track on a map.
                HeadingDegrees = (HeadingDegrees + _random.Next(-20, 21) + 360) % 360;
                var rad = HeadingDegrees * Math.PI / 180;
                Latitude += km / 111.0 * Math.Cos(rad);
                Longitude += km / (111.0 * Math.Cos(Latitude * Math.PI / 180)) * Math.Sin(rad);
                Touch(FordConnectTelemetry.DrivingSignals, sampled);
            }
            else
            {
                Speed = 0;
            }

            if (ChargeStatus is "ChargingAC" or "ChargingDC")
            {
                var hours = Math.Max(0.0, (now - _stamps[FordConnectTelemetry.StateOfChargeSignal]).TotalHours);
                // 11 kW into an 88 kWh pack — the Mach-E's onboard AC charger.
                StateOfCharge = Math.Min(100, StateOfCharge + hours * 11.0 / Seed.CapacityKwh * 100.0);
                if (StateOfCharge >= 99.5) ChargeStatus = "Done";
                Touch(FordConnectTelemetry.ChargingSignals, sampled);
            }

            // Burst when awake, silence when parked. The numbers are the measured ones:
            // 28 s at the tightest, and three quarters of an hour parked.
            var gap = Awake
                ? TimeSpan.FromSeconds(_random.Next(28, 150))
                : TimeSpan.FromMinutes(_random.Next(30, 46));
            _nextReport = now + gap;
            _envelope = now;
        }
    }

    public void Set(string? ignition, string? plug, string? charge, double? soc)
    {
        lock (_gate)
        {
            var now = _now();
            if (ignition is { Length: > 0 }) { Ignition = ignition; Touch([FordConnectTelemetry.IgnitionSignal], now); }
            if (plug is { Length: > 0 }) { Plug = plug; Touch(FordConnectTelemetry.ChargingSignals, now); }
            if (charge is { Length: > 0 }) { ChargeStatus = charge; Touch(FordConnectTelemetry.ChargingSignals, now); }
            if (soc is { } value) { StateOfCharge = Math.Clamp(value, 0, 100); Touch(FordConnectTelemetry.ChargingSignals, now); }
            // A control change is news, so the car reports it at the next read rather
            // than whenever its own schedule came round. Waiting 40 minutes to see a
            // button take effect would make the portal useless.
            _nextReport = now;
            _envelope = now;
        }
    }

    /// <summary>Drive a fixed distance immediately. The portal's "drive 10 km" button:
    /// a whole trip's worth of movement without waiting for one.</summary>
    public void Drive(double km)
    {
        lock (_gate)
        {
            var now = _now();
            Odometer += km;
            StateOfCharge = Math.Max(0, StateOfCharge - km * 0.25);
            var rad = HeadingDegrees * Math.PI / 180;
            Latitude += km / 111.0 * Math.Cos(rad);
            Longitude += km / (111.0 * Math.Cos(Latitude * Math.PI / 180)) * Math.Sin(rad);
            Touch(FordConnectTelemetry.DrivingSignals, now);
            _nextReport = now;
            _envelope = now;
        }
    }

    private void Touch(IEnumerable<string> names, DateTimeOffset at)
    {
        foreach (var name in names) _stamps[name] = at;
    }
}

/// <summary>
/// The whole emulated account: the credential, the cars, the bucket, and the
/// short-lived grants that a consent flow hands out.
/// </summary>
public sealed class FordConnectState
{
    private readonly ConcurrentDictionary<string, FordConnectCar> _cars = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (string Owner, string RedirectUri, DateTimeOffset Expires)> _codes = new();
    private readonly ConcurrentDictionary<string, string> _accessTokens = new();
    private readonly ConcurrentDictionary<string, string> _refreshTokens = new();
    private readonly ConcurrentQueue<FordConnectCall> _calls = new();

    public FordConnectState(FordConnectSeed seed, IConfiguration? configuration = null)
    {
        Seed = seed;
        PublicUrl = configuration?["FORDCONNECT_PUBLIC_URL"]?.TrimEnd('/');
        Bucket = new FordConnectBucket(seed.Behaviour, () => DateTimeOffset.UtcNow);

        if (string.Equals(seed.Behaviour.Mode, "Replay", StringComparison.OrdinalIgnoreCase))
        {
            // Fail here, loudly, rather than serve a car that never reports. The
            // container crash-loops and the reason is in its log, which is the shortest
            // path from "my replay is not playing" to knowing why.
            Replay = new FordConnectReplay(
                seed.Behaviour.ReplayPath
                    ?? throw new InvalidOperationException("Replay mode without a replayPath."),
                seed.Behaviour.ReplaySpeed);
        }

        // In replay the VINs come from the log and REPLACE the seeded ones. Anything
        // else would put a car in the garage with no recording behind it, and the
        // count matters: Ford answers /garage with a bare object for one car and an
        // array for several, so a phantom second car hides that trap.
        var vehicles = Replay is null
            ? seed.Vehicles
            : Replay.Vins.Select(vin => (seed.Vehicles.FirstOrDefault() ?? new FordConnectVehicle(vin))
                with { Vin = vin }).ToList();

        foreach (var vehicle in vehicles)
            _cars[vehicle.Vin] = new FordConnectCar(vehicle, () => DateTimeOffset.UtcNow);

        // A flow nobody can complete is not a local flow. With no owner seeded the
        // authorize page still offers one, so the consent hop works before anything is
        // configured — same reasoning as the tenant emulator accepting an unseeded
        // address at its sign-in page.
        Owners = seed.Owners.Count > 0
            ? seed.Owners
            : [new FordConnectOwner("owner-1", "Emuleret ejer", "owner@example.invalid")];
    }

    public FordConnectSeed Seed { get; }
    public string? PublicUrl { get; }
    public FordConnectBucket Bucket { get; }

    /// <summary>Non-null in replay mode, and then it — not the simulated car — is what
    /// <c>/telemetry</c> answers from.</summary>
    public FordConnectReplay? Replay { get; }
    public IReadOnlyList<FordConnectOwner> Owners { get; }
    public IReadOnlyCollection<FordConnectCar> Cars => _cars.Values.ToList();

    public FordConnectCar? Car(string vin) => _cars.TryGetValue(vin, out var car) ? car : null;

    public IReadOnlyList<FordConnectCall> RecentCalls => _calls.Reverse().Take(40).ToList();

    public void Record(FordConnectCall call)
    {
        _calls.Enqueue(call);
        while (_calls.Count > 200) _calls.TryDequeue(out _);
    }

    // ---------------------------------------------------------------- the grants

    public string MintCode(string owner, string redirectUri)
    {
        var code = Guid.NewGuid().ToString("N");
        _codes[code] = (owner, redirectUri, DateTimeOffset.UtcNow.AddMinutes(10));
        return code;
    }

    public bool TryRedeemCode(string code, out string owner)
    {
        owner = "";
        // Consume once, so a replayed callback cannot mint a second link. The same
        // rule the client's own `state` follows, on the other side of the hop.
        if (!_codes.TryRemove(code, out var row)) return false;
        if (row.Expires < DateTimeOffset.UtcNow) return false;
        owner = row.Owner;
        return true;
    }

    public (string Access, string Refresh) MintTokens(string owner)
    {
        // Long opaque strings, because the real ones are: 1474 and 1592 characters.
        // A client that put a token in a varchar(255) should discover that here.
        var access = $"emu-access-{Guid.NewGuid():N}{new string('x', 1400)}";
        var refresh = $"emu-refresh-{Guid.NewGuid():N}{new string('y', 1500)}";
        _accessTokens[access] = owner;
        _refreshTokens[refresh] = owner;
        return (access, refresh);
    }

    public bool TryOwnerForAccessToken(string token, out string owner) => _accessTokens.TryGetValue(token, out owner!);

    public bool TryRotateRefresh(string refresh, out string owner, out string? nextRefresh)
    {
        nextRefresh = null;
        if (!_refreshTokens.TryGetValue(refresh, out owner!)) return false;

        if (!Seed.Behaviour.RotateRefreshToken) return true;
        _refreshTokens.TryRemove(refresh, out _);
        nextRefresh = $"emu-refresh-{Guid.NewGuid():N}{new string('y', 1500)}";
        _refreshTokens[nextRefresh] = owner;
        return true;
    }

    public void Revoke()
    {
        // What "consent withdrawn in the Ford app" looks like from outside: every
        // token stops working at once, and the client has to tell that apart from
        // being rate limited. It is the state a link-health check exists for.
        _accessTokens.Clear();
        _refreshTokens.Clear();
    }
}

public static class FordConnectEmulatorExtensions
{
    /// <summary>Case-insensitive on purpose. The hosting extension writes PascalCase and
    /// a human writing the seed by hand writes camelCase; with the default comparer the
    /// second one deserialises into a record of nulls that constructs fine and then
    /// NullReferences on first use, which is a bad afternoon.</summary>
    private static readonly JsonSerializerOptions SeedJson = new(JsonSerializerDefaults.Web);

    public static IServiceCollection AddFordConnectEmulator(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var json = configuration["FORDCONNECT_SEED_JSON"];
        var seed = string.IsNullOrWhiteSpace(json)
            ? new FordConnectSeed(
                "fordconnect-emulator",
                "local-only-secret",
                [new FordConnectVehicle("EMULATED0000000001", "Emuleret Mach-E")],
                [],
                new FordConnectBehaviour(5, 15_000, false, false, 1_200, false, "Simulate", null, 1.0))
            : JsonSerializer.Deserialize<FordConnectSeed>(json, SeedJson)
              ?? throw new InvalidOperationException("FORDCONNECT_SEED_JSON is not a FordConnectSeed.");

        if (seed.Behaviour is null)
            throw new InvalidOperationException(
                "FORDCONNECT_SEED_JSON has no `behaviour`. Every measured default lives in there, so an "
                + "emulator without it would be more permissive than Ford — which is the one thing it must never be.");
        if (seed.Vehicles is null || seed.Owners is null)
            throw new InvalidOperationException("FORDCONNECT_SEED_JSON needs `vehicles` and `owners` arrays.");

        services.AddSingleton(new FordConnectState(seed, configuration));
        return services;
    }

    public static IEndpointRouteBuilder MapFordConnectEmulator(this IEndpointRouteBuilder app)
    {
        app.MapGet("/healthz", () => Results.Ok(new { ok = true }));

        MapAuthorize(app);
        MapToken(app);
        MapQuery(app);
        return app;
    }

    // ---------------------------------------------------------- the front door
    //
    // The step every FordConnect integration falls over. The real flow does NOT start
    // at Azure B2C's authorize endpoint even though B2C is behind it: `auth/init` 302s
    // to `login.ford.com` carrying a `ford_application_id`, a `country_code` and Ford's
    // own FMA client id, none of which a caller could supply. Skipping it fails with
    // `AADB2C90075 RESTApiCallForUserInfo` *before any user is involved*, which reads
    // like a broken credential and is not.
    //
    // Here the door renders a consent page instead of a redirect, because the point
    // locally is to be able to click through it without a Ford account. `locale` is
    // still required — Ford answers `400 Invalid locale: _` without it — so a client
    // that drops query parameters from the authorize URL fails here too.

    private static void MapAuthorize(IEndpointRouteBuilder app)
    {
        app.MapGet("/fcon-public/v1/auth/init", (HttpRequest req, FordConnectState state) =>
        {
            var q = req.Query;
            if (string.IsNullOrWhiteSpace(q["locale"]))
                return Results.Json(new { error = "invalid_request", message = "Invalid locale: _" }, statusCode: 400);

            var clientId = q["client_id"].ToString();
            if (clientId != state.Seed.ClientId)
                return Results.Json(new { error = "unauthorized_client", message = $"Unknown client {clientId}" }, statusCode: 400);

            var redirectUri = q["redirect_uri"].ToString();
            if (string.IsNullOrWhiteSpace(redirectUri))
                return Results.Json(new { error = "invalid_request", message = "redirect_uri is required" }, statusCode: 400);

            var stateParam = q["state"].ToString();
            state.Record(new(DateTimeOffset.UtcNow, "auth/init", 200, state.Bucket.TokensLeft, "consent page shown"));
            return Results.Content(FordConnectPortal.ConsentPage(state, redirectUri, stateParam), "text/html; charset=utf-8");
        });

        // What the consent page's button posts back to. Not part of Ford's surface —
        // Ford has a whole login journey here — so it lives under /emulator/ where it
        // cannot be mistaken for an endpoint worth coding against.
        app.MapPost("/emulator/consent", async (HttpRequest req, FordConnectState state) =>
        {
            var form = await req.ReadFormAsync();
            var owner = form["owner"].ToString();
            var redirectUri = form["redirect_uri"].ToString();
            var stateParam = form["state"].ToString();

            if (form["decision"] == "deny")
            {
                // A refusal is a real outcome and comes back on the redirect, not as an
                // error page. A client that only handles `code` hangs on this.
                var denied = new UriBuilder(redirectUri);
                denied.Query = Merge(denied.Query, $"error=access_denied&state={Uri.EscapeDataString(stateParam)}");
                state.Record(new(DateTimeOffset.UtcNow, "consent", 302, state.Bucket.TokensLeft, "owner refused"));
                return Results.Redirect(denied.ToString());
            }

            var code = state.MintCode(owner, redirectUri);
            var target = new UriBuilder(redirectUri);
            target.Query = Merge(target.Query, $"code={code}&state={Uri.EscapeDataString(stateParam)}");
            state.Record(new(DateTimeOffset.UtcNow, "consent", 302, state.Bucket.TokensLeft, $"granted to {owner}"));
            return Results.Redirect(target.ToString());
        });
    }

    private static string Merge(string existing, string added)
    {
        var head = existing.TrimStart('?');
        return head.Length == 0 ? added : $"{head}&{added}";
    }

    // ------------------------------------------------------------------ tokens
    //
    // POST, form-encoded, credentials in the BODY rather than Basic auth — that is how
    // Ford's B2C endpoint wants them, and a client built for Basic gets a 401 that
    // says nothing useful.

    private static void MapToken(IEndpointRouteBuilder app)
    {
        app.MapPost("/dah2vb2cprod.onmicrosoft.com/oauth2/v2.0/token", async (HttpRequest req, FordConnectState state) =>
        {
            var form = await req.ReadFormAsync();

            if (form["client_id"] != state.Seed.ClientId || form["client_secret"] != state.Seed.ClientSecret)
                return Results.Json(new
                {
                    error = "invalid_client",
                    error_description = "AADSTS7000215: Invalid client secret provided.",
                }, statusCode: 401);

            var grant = form["grant_type"].ToString();
            string owner;
            string? refresh = null;

            if (grant == "authorization_code")
            {
                if (!state.TryRedeemCode(form["code"].ToString(), out owner))
                    return Results.Json(new
                    {
                        error = "invalid_grant",
                        error_description = "AADB2C90080: The provided grant has expired or has already been redeemed.",
                    }, statusCode: 400);

                var minted = state.MintTokens(owner);
                return TokenResponse(state, minted.Access, minted.Refresh);
            }

            if (grant == "refresh_token")
            {
                if (!state.TryRotateRefresh(form["refresh_token"].ToString(), out owner, out refresh))
                    return Results.Json(new
                    {
                        error = "invalid_grant",
                        error_description = "AADB2C90080: The provided grant has expired or has already been redeemed.",
                    }, statusCode: 400);

                var minted = state.MintTokens(owner);
                // Ford's refresh response sometimes omits `refresh_token`, and a client
                // that overwrites blindly stores undefined and dies silently at the
                // *next* refresh. Omitting it is the default here for exactly that
                // reason — the bug should surface locally, not two hours into a demo.
                return TokenResponse(state, minted.Access, state.Seed.Behaviour.RotateRefreshToken ? refresh : null);
            }

            return Results.Json(new
            {
                error = "unsupported_grant_type",
                error_description = $"grant_type '{grant}' is not supported",
            }, statusCode: 400);
        });
    }

    private static IResult TokenResponse(FordConnectState state, string access, string? refresh)
    {
        var body = new Dictionary<string, object>
        {
            ["access_token"] = access,
            ["token_type"] = "Bearer",
            // Twenty minutes, measured. Not the hour most OAuth code assumes, and the
            // difference is what makes a background refresh loop mandatory rather than
            // a nicety.
            ["expires_in"] = state.Seed.Behaviour.AccessTokenSeconds,
            ["scope"] = $"{state.Seed.ClientId} openid offline_access",
        };
        if (refresh is { Length: > 0 }) body["refresh_token"] = refresh;
        return Results.Json(body);
    }

    // --------------------------------------------------------------- the reads

    private static void MapQuery(IEndpointRouteBuilder app)
    {
        var query = app.MapGroup("/fcon-query/v1");

        query.MapGet("/garage", (HttpRequest req, FordConnectState state) =>
            Guard(req, state, "garage", owner =>
            {
                var rows = state.Cars.Select(car => new Dictionary<string, object?>
                {
                    ["vin"] = car.Vin,
                    ["vehicleId"] = car.VehicleId,
                    ["nickName"] = car.Seed.NickName,
                    ["modelName"] = car.Seed.ModelName,
                    ["modelYear"] = car.Seed.ModelYear,
                    ["color"] = car.Seed.Color,
                    ["engineType"] = car.Seed.EngineType,
                    ["make"] = "Ford",
                }).ToList();

                // A bare object for ONE car and an array for several. Ford really does
                // this, and a client that only handles the array shape shows an empty
                // garage to every single-car account — which is most of them.
                return rows.Count == 1 ? (object)rows[0] : rows;
            }));

        query.MapGet("/telemetry", (HttpRequest req, FordConnectState state) =>
            PerVehicle(req, state, "telemetry", car =>
            {
                // The recorded payload wins where there is one, unchanged. Both modes go
                // through the same guard above, so replay costs rate-limit tokens like
                // everything else — a cadence experiment against a recording that was
                // not also rate limited would come out with the wrong answer.
                if (state.Replay is { } replay)
                    return replay.Telemetry(car.Vin)
                        ?? throw new FordConnectRefusal(404, $"No recorded readings for {car.Vin}");

                car.Tick();
                return FordConnectTelemetry.Build(car);
            }));

        query.MapGet("/vehicle-health/alerts", (HttpRequest req, FordConnectState state) =>
            PerVehicle(req, state, "alerts", car => new { vin = car.Vin, alerts = Array.Empty<object>() }));

        // Empty on a real credential without a Ford wallbox. Answering with plausible
        // rows would be the worst kind of fixture: it makes a screen look finished and
        // the same screen is blank in production.
        query.MapGet("/wallbox", (HttpRequest req, FordConnectState state) =>
            PerVehicle(req, state, "wallbox", _ => new { rows = Array.Empty<object>() }));

        query.MapGet("/electric/departure-times", (HttpRequest req, FordConnectState state) =>
            PerVehicle(req, state, "departureTimes", car => new { vin = car.Vin, departureTimes = Array.Empty<object>() }));

        query.MapGet("/electric/charge-schedules", (HttpRequest req, FordConnectState state) =>
            PerVehicle(req, state, "chargeSchedules", car => new { vin = car.Vin, chargeSchedules = Array.Empty<object>() }));

        // Named and refused, the way it is refused for real: `fccs` wants a
        // `chargingStationId` header nobody without a Ford charger has.
        query.MapGet("/fccs", () => Results.Json(new
        {
            message = "chargingStationId header is required",
        }, statusCode: 400));
    }

    private static IResult PerVehicle(
        HttpRequest req,
        FordConnectState state,
        string endpoint,
        Func<FordConnectCar, object> project) =>
        Guard(req, state, endpoint, _ =>
        {
            var vin = req.Query["vin"].ToString();
            if (string.IsNullOrWhiteSpace(vin))
                throw new FordConnectRefusal(400, "vin is a required query parameter");

            var car = state.Car(vin)
                ?? throw new FordConnectRefusal(404, $"No vehicle {vin} on this account");

            return project(car);
        });

    /// <summary>
    /// Authorisation and the rate limit, in the order Ford applies them, for every
    /// Query endpoint.
    ///
    /// The 429 body carries a message and a <c>traceId</c> and NO headers — not
    /// <c>Retry-After</c>, not a limit or remaining count. There is nothing to read,
    /// which is why a client has to guess the budget, and why a client that reads a
    /// header here works locally and starves in production.
    /// </summary>
    private static IResult Guard(
        HttpRequest req,
        FordConnectState state,
        string endpoint,
        Func<string, object> project)
    {
        var header = req.Headers.Authorization.ToString();
        var token = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header[7..] : "";

        if (!state.TryOwnerForAccessToken(token, out var owner))
        {
            state.Record(new(DateTimeOffset.UtcNow, endpoint, 401, state.Bucket.TokensLeft, "unknown or revoked token"));
            return Results.Json(new { message = "Unauthorized" }, statusCode: 401);
        }

        if (!state.Bucket.TryTake())
        {
            var traceId = Guid.NewGuid().ToString();
            state.Record(new(DateTimeOffset.UtcNow, endpoint, 429, 0,
                $"shared bucket empty, {state.Bucket.WaitMs} ms to go"));

            var refusal = Results.Json(new
            {
                message = "Too many requests. Please wait 30 seconds before trying again.",
                traceId,
            }, statusCode: 429);

            // Off by default because Ford sends nothing. On, only to see what a
            // well-behaved API would have told you.
            return state.Seed.Behaviour.SendRateLimitHeaders
                ? new HeaderedResult(refusal, state.Bucket.WaitMs)
                : refusal;
        }

        try
        {
            var body = project(owner);
            state.Record(new(DateTimeOffset.UtcNow, endpoint, 200, state.Bucket.TokensLeft, null));
            return Results.Json(body);
        }
        catch (FordConnectRefusal refusal)
        {
            state.Record(new(DateTimeOffset.UtcNow, endpoint, refusal.Status, state.Bucket.TokensLeft, refusal.Message));
            return Results.Json(new { message = refusal.Message }, statusCode: refusal.Status);
        }
    }

    private sealed class HeaderedResult(IResult inner, int waitMs) : IResult
    {
        public Task ExecuteAsync(HttpContext http)
        {
            http.Response.Headers["Retry-After"] = Math.Max(1, waitMs / 1000).ToString();
            http.Response.Headers["X-RateLimit-Remaining"] = "0";
            return inner.ExecuteAsync(http);
        }
    }
}

/// <summary>A refusal that is Ford's, not the transport's — a missing vin, an unknown
/// car. Thrown from inside the guard so the rate-limit token is already spent, which
/// is what Ford does: a malformed request still costs you.</summary>
public sealed class FordConnectRefusal(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}
