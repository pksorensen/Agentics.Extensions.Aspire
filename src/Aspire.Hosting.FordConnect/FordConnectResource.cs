using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.FordConnect;

/// <summary>
/// A local stand-in for FordConnect 2.0 — the account-link door, the token endpoint
/// and the Query API — as one Aspire resource.
///
/// It exists because the real thing cannot be developed against. Three reasons, in
/// order of how quickly they stop you:
///
///   1. <c>login.ford.com</c> is browser-only (Akamai) and times out from a
///      devcontainer and from a server alike, so the consent hop cannot be completed
///      from a dev box at all.
///   2. Every Query endpoint shares one rate-limit bucket with a burst of four or
///      five and a refill of one call per fifteen seconds. Two developers and a test
///      suite on one credential is not a working arrangement.
///   3. The data is one household's real car, including — deliberately, now — where
///      it has been.
/// </summary>
public sealed class FordConnectResource : ContainerResource
{
    internal FordConnectResource(string name) : base(name) { }

    public const string HttpEndpointName = "http";

    /// <summary>The address the browser and the app under test reach this on. Null
    /// leaves the read side working and the consent flow unavailable — see
    /// <see cref="FordConnectOptions.HostPort"/>.</summary>
    public string? PublicUrl { get; init; }

    /// <summary>The credential the app under test authenticates with. Local-only by
    /// construction: the emulator accepts exactly this pair and nothing else, so a
    /// real Ford secret in an AppHost would be both a leak and pointless.</summary>
    public string ClientId { get; init; } = "fordconnect-emulator";
    public string ClientSecret { get; init; } = "local-only-secret";

    public IList<FordConnectVehicle> Vehicles { get; } = [];
    public IList<FordConnectOwner> Owners { get; } = [];

    /// <summary>Set by <c>WithReplayLog</c>: the container path of the mounted JSONL
    /// export, and how fast to play it. Kept on the resource rather than in the options
    /// because the mount is added after the resource exists, and the seed environment is
    /// built lazily — so whichever is set last wins, which is what a reader expects of
    /// two calls in a row.</summary>
    internal string? ReplayPath { get; set; }
    internal double? ReplaySpeed { get; set; }
}
