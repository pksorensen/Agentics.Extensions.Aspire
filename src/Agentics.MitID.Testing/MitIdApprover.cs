namespace Agentics.MitID.Testing;

/// <summary>
/// One call a test can make at the moment a MitID login is waiting:
/// <c>await approver.ApproveAsync("commuteconnects", "Oscar39838", TimeSpan.FromSeconds(30))</c>.
/// </summary>
/// <remarks>
/// <para>
/// There are two ways to reach that, and they differ in one thing only — who
/// remembers which test users exist.
/// </para>
/// <list type="bullet">
///   <item>
///     <description>
///     <see cref="Direct"/> drives pp.mitid.dk itself. It needs nothing you
///     operate and no secret at all: MitID hands its own client credentials to
///     any caller. The test must name the user by bruger-ID, and the PIN is the
///     Test Tool's published default unless you say otherwise.
///     </description>
///   </item>
///   <item>
///     <description>
///     <see cref="ViaService"/> goes through an agent-mitid instance, which keeps
///     a per-project registry. The test names a project and a bruger-ID; the
///     identity, the authenticator and the PIN come from the registry, so the
///     test survives someone recreating the user. Costs a bearer token in CI.
///     </description>
///   </item>
/// </list>
/// <para>
/// <see cref="FromEnvironment"/> picks the second when it is configured and falls
/// back to the first, which is usually what a suite wants: the shared registry on
/// a developer machine and in CI, and a working test on a laptop with no token.
/// </para>
/// <para>
/// <b>Pre-production only.</b> The identities involved are invented by MitID's own
/// test-person generator and refer to nobody; the host is a constant, not a
/// setting. Nothing here works against, or is meant to resemble, production MitID.
/// </para>
/// </remarks>
public sealed class MitIdApprover
{
    private readonly MitIdCodeApp? _direct;
    private readonly AgentMitIdClient? _service;
    private readonly string? _pin;

    private MitIdApprover(MitIdCodeApp? direct, AgentMitIdClient? service, string? pin)
    {
        _direct = direct;
        _service = service;
        _pin = pin;
    }

    /// <summary>Drives pp.mitid.dk directly. No registry, no token, no service.</summary>
    /// <param name="pin">The code app's PIN, if it is not the Test Tool default.</param>
    /// <param name="http">An optional client to borrow.</param>
    public static MitIdApprover Direct(string? pin = null, HttpClient? http = null)
        => new(new MitIdCodeApp(http), null, pin);

    /// <summary>Goes through an agent-mitid instance and its registry.</summary>
    /// <param name="serviceUrl">The service origin, e.g. <c>https://mitid.agentics.dk</c>.</param>
    /// <param name="token">The MCP bearer token.</param>
    /// <param name="http">An optional client to borrow.</param>
    public static MitIdApprover ViaService(Uri serviceUrl, string token, HttpClient? http = null)
        => new(null, new AgentMitIdClient(serviceUrl, token, http), null);

    /// <summary>
    /// Reads <c>MITID_SERVICE_URL</c> and <c>MITID_MCP_TOKEN</c>; uses the service
    /// when both are set and falls back to <see cref="Direct"/> when they are not.
    /// <c>MITID_PIN</c> overrides the default PIN in direct mode.
    /// </summary>
    /// <remarks>
    /// The Aspire hosting integration sets these variables on referencing
    /// projects, so a test that calls this needs no configuration of its own.
    /// </remarks>
    public static MitIdApprover FromEnvironment(HttpClient? http = null)
    {
        var url = Environment.GetEnvironmentVariable("MITID_SERVICE_URL");
        var token = Environment.GetEnvironmentVariable("MITID_MCP_TOKEN");
        if (!string.IsNullOrWhiteSpace(url) && !string.IsNullOrWhiteSpace(token))
            return ViaService(new Uri(url, UriKind.Absolute), token, http);
        return Direct(Environment.GetEnvironmentVariable("MITID_PIN"), http);
    }

    /// <summary>True when this approver is backed by an agent-mitid registry.</summary>
    public bool UsesRegistry => _service is not null;

    /// <summary>
    /// Approves the MitID login waiting for this user.
    /// </summary>
    /// <param name="project">
    /// The project key in the agent-mitid registry. Ignored in direct mode, where
    /// the bruger-ID alone identifies the user.
    /// </param>
    /// <param name="userId">The bruger-ID typed on the MitID login page.</param>
    /// <param name="wait">
    /// How long to keep looking. Call this straight after clicking
    /// <c>"Åbn app på anden enhed"</c> — that click is what dispatches the
    /// transaction, so approving before it finds nothing no matter how long it
    /// polls.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<MitIdApproval> ApproveAsync(string project, string userId, TimeSpan wait = default, CancellationToken ct = default)
    {
        if (_service is not null) return await _service.ApproveAsync(project, userId, wait, ct).ConfigureAwait(false);
        var user = await _direct!.FindAsync(userId, _pin, ct).ConfigureAwait(false);
        return await _direct.ApproveAsync(user, wait, ct).ConfigureAwait(false);
    }

    /// <summary>Refuses the MitID login waiting for this user.</summary>
    public async Task<MitIdApproval> RejectAsync(string project, string userId, TimeSpan wait = default, CancellationToken ct = default)
    {
        if (_service is not null) return await _service.RejectAsync(project, userId, wait, ct).ConfigureAwait(false);
        var user = await _direct!.FindAsync(userId, _pin, ct).ConfigureAwait(false);
        return await _direct.RejectAsync(user, wait, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Opens a time-boxed window in which the service approves this user's logins
    /// unattended — for a login triggered somewhere the test cannot call approve.
    /// Requires <see cref="UsesRegistry"/>; there is no server to arm in direct mode.
    /// </summary>
    public Task AutoApproveAsync(string project, string userId, TimeSpan window, CancellationToken ct = default)
        => _service is not null
            ? _service.AutoApproveAsync(project, userId, window, ct)
            : throw new MitIdException(
                "auto-approve needs an agent-mitid service — in direct mode there is no server to do the watching. " +
                "Use ApproveAsync with a wait instead, or configure MITID_SERVICE_URL and MITID_MCP_TOKEN.");
}
