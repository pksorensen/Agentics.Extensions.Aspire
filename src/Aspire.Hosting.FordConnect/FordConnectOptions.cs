namespace Aspire.Hosting.FordConnect;

/// <summary>
/// How the emulated FordConnect behaves.
///
/// Every default here is a measurement, not a guess. They were taken against a real
/// 2022 Mustang Mach-E on a real credential in August 2026, and they are the reason
/// this emulator is worth having: an app that works against a permissive stub and
/// then meets Ford's actual rate limit has learned nothing.
/// </summary>
public sealed class FordConnectOptions
{
    public string Image { get; set; } = "ghcr.io/pksorensen/fordconnect-emulator";
    public string Tag { get; set; } = "latest";
    public string? DockerfileContext { get; set; }
    public string DockerfilePath { get; set; } = "src/FordConnect.Emulator/Dockerfile";
    public bool PersistData { get; set; }
    public string? DataVolumeName { get; set; }

    /// <summary>
    /// A fixed host port for the emulator, published without the Aspire proxy.
    ///
    /// FordConnect is entered through a *browser*: the app sends the owner to an
    /// authorize URL, and the emulator has to send them back to the app's callback.
    /// Both hops therefore happen at addresses a human's browser can reach, which a
    /// proxied, port-shuffling endpoint is not. Same reason the Microsoft tenant
    /// emulator pins its port.
    ///
    /// Leave it null and the read side still works; the consent flow does not.
    /// </summary>
    public int? HostPort { get; set; }

    /// <summary>The address the browser and the app under test use, when it is not
    /// <c>http://localhost:{HostPort}</c> — a tunnel in front of a dev box.</summary>
    public string? PublicUrl { get; set; }

    // ------------------------------------------------------------ the rate limit

    /// <summary>
    /// Tokens available in a cold bucket. Measured at four to five; five is the
    /// generous reading and the one that fails a caller who assumed ten.
    ///
    /// The bucket is shared by every Query endpoint. That is the single most
    /// important thing this emulator reproduces, because the intuitive design — one
    /// cadence per endpoint — is wrong in a way nothing else reveals: hammering
    /// <c>wallbox</c> to 429 also 429s <c>telemetry</c>, <c>alerts</c> and
    /// <c>garage</c> on their first call, untouched.
    /// </summary>
    public int RateLimitBurst { get; set; } = 5;

    /// <summary>Milliseconds per refilled token. Measured at one per fifteen
    /// seconds, and it does not accumulate — after pauses of 15 s, 30 s and 60 s
    /// there was exactly one call available each time, never four.</summary>
    public int RateLimitRefillMs { get; set; } = 15_000;

    /// <summary>
    /// Whether the bucket accumulates unused tokens up to <see cref="RateLimitBurst"/>.
    ///
    /// False is the measured behaviour, and the harsher one: a caller that idles for
    /// ten minutes does not earn a burst. Turn it on only to see what a well-behaved
    /// token bucket would have felt like.
    /// </summary>
    public bool RateLimitAccumulates { get; set; }

    /// <summary>
    /// Send <c>Retry-After</c> and rate-limit headers on a 429.
    ///
    /// Ford sends none — not on 200, not on 429, not even <c>Retry-After</c>. Only a
    /// body with a message and a <c>traceId</c>. False is therefore the faithful
    /// setting, and it is the one that catches a client which reads a header it will
    /// never receive in production.
    /// </summary>
    public bool SendRateLimitHeaders { get; set; }

    // ---------------------------------------------------------------- the tokens

    /// <summary>Access-token lifetime in seconds. Measured at twenty minutes, not
    /// the hour most OAuth code is written against.</summary>
    public int AccessTokenSeconds { get; set; } = 1_200;

    /// <summary>
    /// Whether a refresh response includes a new refresh token.
    ///
    /// Ford's sometimes omits it. A client that overwrites blindly then stores
    /// <c>undefined</c> and the link dies silently at the *next* refresh, with no
    /// error anywhere — so this defaults to the shape that exposes the bug.
    /// </summary>
    public bool RotateRefreshToken { get; set; }

    // ------------------------------------------------------------------ the car

    /// <summary>
    /// Where the simulated car's telemetry comes from.
    ///
    /// <c>Simulate</c> drives a synthetic Mach-E: ignition, plug, a battery that
    /// drains and charges, and — most importantly — Ford's *reporting* behaviour,
    /// which is not periodic. A parked car goes quiet for the better part of an hour;
    /// an awake one reports three times in five minutes, tightest measured gap 28
    /// seconds. Anything that tunes a polling cadence has to meet that shape.
    ///
    /// <c>Replay</c> serves a recorded event log instead — the <c>ford.reading</c>
    /// rows this platform already writes. Point <see cref="ReplayPath"/> at an
    /// exported JSONL file. Do not commit one: they carry a real VIN and, now that
    /// the log keeps position, a real home address.
    /// </summary>
    public FordConnectDataMode Mode { get; set; } = FordConnectDataMode.Simulate;

    /// <summary>Container path to a JSONL export for <see cref="FordConnectDataMode.Replay"/>.</summary>
    public string? ReplayPath { get; set; }

    /// <summary>Replay wall-clock speed. 60 turns an hour of log into a minute, which
    /// is how a cadence experiment becomes a test instead of an afternoon.</summary>
    public double ReplaySpeed { get; set; } = 1.0;
}

public enum FordConnectDataMode
{
    Simulate,
    Replay,
}

/// <summary>
/// A car on the emulated account.
///
/// <paramref name="Vin"/> is the account key and the <c>vin</c> query parameter every
/// per-vehicle endpoint takes. Use an obviously fake one: a real VIN in an AppHost is
/// a real VIN in git.
/// </summary>
public sealed record FordConnectVehicle(
    string Vin,
    string? NickName = null,
    string ModelName = "Mustang Mach-E",
    string ModelYear = "2022",
    string Color = "Space White",
    string EngineType = "BEV",
    double StateOfCharge = 76.5,
    double CapacityKwh = 88.0,
    double Odometer = 99_878,
    double Latitude = 55.6761,
    double Longitude = 12.5683);

/// <summary>The owner who grants consent. The emulator's authorize page lists these
/// and signs in as the one that is clicked — the same shape as the Microsoft tenant
/// emulator's sign-in-as picker, and for the same reason: a consent flow nobody can
/// complete without a password is not a local flow.</summary>
public sealed record FordConnectOwner(
    string Id,
    string DisplayName,
    string Email);

public sealed record FordConnectSeed(
    string ClientId,
    string ClientSecret,
    IReadOnlyList<FordConnectVehicle> Vehicles,
    IReadOnlyList<FordConnectOwner> Owners,
    FordConnectBehaviour Behaviour);

/// <summary>The measured facts, as the emulator receives them.</summary>
public sealed record FordConnectBehaviour(
    int RateLimitBurst,
    int RateLimitRefillMs,
    bool RateLimitAccumulates,
    bool SendRateLimitHeaders,
    int AccessTokenSeconds,
    bool RotateRefreshToken,
    string Mode,
    string? ReplayPath,
    double ReplaySpeed);
