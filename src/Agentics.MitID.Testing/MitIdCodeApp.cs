using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Agentics.MitID.Testing;

/// <summary>
/// Plays the MitID code app directly against pp.mitid.dk: finds a test identity
/// from its bruger-ID, waits for a transaction, and approves or refuses it.
/// </summary>
/// <remarks>
/// <para>
/// The whole code-app side is plain REST. Every value posted comes out of a
/// previous response and the test backend signs on the app's behalf, so there is
/// no client-side cryptography here — ten calls, about a second, no browser.
/// </para>
/// <para>
/// Two of those calls need no credentials at all; the administrative search needs
/// a token, and that token is minted from client credentials the environment
/// hands to any caller via <c>GET /mitid-test-api/v1/app-config</c>. Nothing in
/// this class is a secret of yours, which is why it can live in a public package.
/// </para>
/// </remarks>
public sealed partial class MitIdCodeApp
{
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private string? _token;
    private DateTimeOffset _tokenExpiry;

    /// <summary>Creates a client against MitID pre-production.</summary>
    /// <param name="http">
    /// An optional client to borrow. Its <see cref="HttpClient.BaseAddress"/> is
    /// ignored — every request is absolute against
    /// <see cref="MitIdTestEnvironment.BaseUrl"/>.
    /// </param>
    public MitIdCodeApp(HttpClient? http = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    [GeneratedRegex("^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-")] private static partial Regex UuidLike();
    [GeneratedRegex(@"^\d{10}$")] private static partial Regex CprLike();

    private static string Url(string path) => MitIdTestEnvironment.BaseUrl + path;

    private static string CodeAppRoot(string identityUuid) =>
        $"/mitid-test-api/v4/identities/{identityUuid}/authenticators/code-app";

    // ---- bootstrap ---------------------------------------------------------

    private async Task<string> TokenAsync(CancellationToken ct)
    {
        if (_token is not null && DateTimeOffset.UtcNow < _tokenExpiry) return _token;
        await _tokenLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_token is not null && DateTimeOffset.UtcNow < _tokenExpiry) return _token;

            var cfg = await GetJsonAsync("/mitid-test-api/v1/app-config", auth: false, ct).ConfigureAwait(false);
            var id = cfg?["testtoolClientId"]?.GetValue<string>();
            var secret = cfg?["testtoolClientSecret"]?.GetValue<string>();
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(secret))
                throw new MitIdException("app-config did not contain test-tool client credentials");

            using var req = new HttpRequestMessage(HttpMethod.Post,
                Url("/mitid-administrative-idp/oauth/token?grant_type=client_credentials"));
            req.Headers.Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes($"{id}:{secret}")));
            // The body really is the literal "{}" on a form content type — that is
            // what the Test Tool sends, and the endpoint rejects an empty body.
            req.Content = new StringContent("{}", Encoding.UTF8, "application/x-www-form-urlencoded");

            var node = await SendAsync(req, ct).ConfigureAwait(false);
            _token = node?["access_token"]?.GetValue<string>()
                ?? throw new MitIdException("token endpoint returned no access_token");
            var ttl = node?["expires_in"]?.GetValue<int>() ?? 300;
            _tokenExpiry = DateTimeOffset.UtcNow.AddSeconds(Math.Max(ttl - 60, 60));
            return _token;
        }
        finally { _tokenLock.Release(); }
    }

    // ---- plumbing ----------------------------------------------------------

    private async Task<JsonNode?> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        using var res = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var body = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!res.IsSuccessStatusCode)
        {
            var snippet = body.Length > 300 ? body[..300] + "…" : body;
            throw new MitIdException($"mitid {req.Method} {req.RequestUri?.AbsolutePath}: {(int)res.StatusCode} {snippet}");
        }
        return string.IsNullOrWhiteSpace(body) ? null : JsonNode.Parse(body);
    }

    private async Task<JsonNode?> GetJsonAsync(string path, bool auth, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, Url(path));
        if (auth) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await TokenAsync(ct).ConfigureAwait(false));
        return await SendAsync(req, ct).ConfigureAwait(false);
    }

    private async Task<JsonNode?> PostJsonAsync(string path, object body, bool auth, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, Url(path))
        {
            Content = JsonContent.Create(body, options: JsonOpts),
        };
        if (auth) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await TokenAsync(ct).ConfigureAwait(false));
        return await SendAsync(req, ct).ConfigureAwait(false);
    }

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    // ---- finding a user ----------------------------------------------------

    /// <summary>
    /// Resolves a test user from a bruger-ID, a CPR number or an identity UUID.
    /// </summary>
    /// <remarks>
    /// The obvious-looking <c>GET /mitid-test-api/v4/identities?searchValue=…</c>
    /// answers 200 with zero results for every query, including an identity
    /// created seconds earlier — it fails silently and reads as "no such user".
    /// The real search is the POST used here, and the field name has to match the
    /// kind of value.
    /// </remarks>
    /// <param name="search">Bruger-ID, ten-digit CPR, or identity UUID.</param>
    /// <param name="pin">The code app's PIN. Defaults to the Test Tool's published default.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<MitIdTestUser> FindAsync(string search, string? pin = null, CancellationToken ct = default)
    {
        object body = UuidLike().IsMatch(search)
            ? new { identityId = search }
            : CprLike().IsMatch(search)
                ? new { cprNumber = search, exactMatch = true, entries = 50 }
                : new { userId = search, exactMatch = true, entries = 50 };

        var hits = await PostJsonAsync("/administration/v5/identities", body, auth: true, ct).ConfigureAwait(false);
        var first = hits?["identities"]?.AsArray().FirstOrDefault()
            ?? throw new MitIdException($"no MitID test identity matches \"{search}\" — try the UUID, the CPR or the exact bruger-ID");
        var uuid = first["identityId"]?.GetValue<string>()
            ?? throw new MitIdException("identity search returned a row without an identityId");

        var details = await GetJsonAsync($"/administration/v9/identities/{uuid}", auth: true, ct).ConfigureAwait(false);
        var d = details?.AsArray().FirstOrDefault();

        var auths = await GetJsonAsync($"{CodeAppRoot(uuid)}", auth: true, ct).ConfigureAwait(false);
        var authId = auths?.AsArray().FirstOrDefault()?["authenticatorId"]?.GetValue<string>()
            ?? throw new MitIdException($"identity {uuid} has no enrolled code app to approve with");

        return new MitIdTestUser(
            d?["userId"]?.GetValue<string>() ?? search,
            uuid,
            authId,
            pin ?? MitIdTestEnvironment.DefaultPin,
            d?["identityName"]?.GetValue<string>());
    }

    // ---- the transaction ---------------------------------------------------

    private async Task<(string AuthKey, long Timestamp, JsonNode? Pull)> GrabAsync(MitIdTestUser u, CancellationToken ct)
    {
        var key = await GetJsonAsync($"{CodeAppRoot(u.IdentityUuid)}/simulator/{u.AuthenticatorId}/auth-key", auth: false, ct)
            .ConfigureAwait(false);
        var authKey = key?["authKey"]?.GetValue<string>() ?? throw new MitIdException("auth-key returned no key");
        var ts = key?["timestamp"]?.GetValue<long>() ?? 0;

        var pull = await PostJsonAsync($"{CodeAppRoot(u.IdentityUuid)}/{u.AuthenticatorId}/simulator-notifier/pull",
            new { authkey = authKey, timestamp = ts }, auth: false, ct).ConfigureAwait(false);
        return (authKey, ts, pull);
    }

    private async Task<JsonNode> PerformAuthAsync(MitIdTestUser u, JsonNode pull, CancellationToken ct)
    {
        var msg = pull["msg"];
        var res = await PostJsonAsync($"{CodeAppRoot(u.IdentityUuid)}/{u.AuthenticatorId}/simulator-notifier/perform-auth",
            new
            {
                pIN = u.Pin,
                datagram = msg?["datagram"]?.GetValue<string>(),
                msg = msg?["msg"]?.GetValue<string>(),
                ticket = pull["ticket"]?.GetValue<string>(),
            }, auth: false, ct).ConfigureAwait(false);
        return res ?? throw new MitIdException("perform-auth returned nothing");
    }

    /// <summary>
    /// Reports what is waiting for this user, without approving it.
    /// </summary>
    /// <returns>The reference text, or <c>null</c> when nothing is pending.</returns>
    public async Task<MitIdReference?> PendingAsync(MitIdTestUser user, CancellationToken ct = default)
    {
        var (_, _, pull) = await GrabAsync(user, ct).ConfigureAwait(false);
        if (pull?["status"]?.GetValue<string>() != "OK") return null;
        return ReferenceOf(await PerformAuthAsync(user, pull, ct).ConfigureAwait(false));
    }

    /// <summary>
    /// Approves the pending MitID login for this user — the "GODKEND" button,
    /// played end to end.
    /// </summary>
    /// <param name="user">The test user, from <see cref="FindAsync"/>.</param>
    /// <param name="wait">
    /// How long to keep looking for a transaction. Pass a few seconds and call
    /// this straight after clicking "Åbn app på anden enhed" — that click is what
    /// dispatches the transaction, and approving before it finds nothing.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    public Task<MitIdApproval> ApproveAsync(MitIdTestUser user, TimeSpan wait = default, CancellationToken ct = default)
        => RespondAsync(user, wait, confirmed: true, ct);

    /// <summary>
    /// Refuses the pending MitID login — the same call as approve, one bit apart,
    /// so a test can exercise what the app under test does when the user says no.
    /// </summary>
    public Task<MitIdApproval> RejectAsync(MitIdTestUser user, TimeSpan wait = default, CancellationToken ct = default)
        => RespondAsync(user, wait, confirmed: false, ct);

    private async Task<MitIdApproval> RespondAsync(MitIdTestUser u, TimeSpan wait, bool confirmed, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + (wait > TimeSpan.Zero ? wait : TimeSpan.Zero);
        string authKey;
        long ts;
        JsonNode? pull;
        while (true)
        {
            (authKey, ts, pull) = await GrabAsync(u, ct).ConfigureAwait(false);
            if (pull?["status"]?.GetValue<string>() == "OK") break;
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new MitIdException(
                    "nothing was waiting to approve — check the login page reached \"Åbn MitID app og godkend\" " +
                    "and that \"Åbn app på anden enhed\" was pressed; that screen is a choice, not a wait")
                { NothingPending = true };
            }
            await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
        }

        var ticket = pull!["ticket"]!.GetValue<string>();
        var reference = ReferenceOf(await PerformAuthAsync(u, pull, ct).ConfigureAwait(false));

        var notifier = $"{CodeAppRoot(u.IdentityUuid)}/{u.AuthenticatorId}/simulator-notifier";

        // Channel binding is two-phase: claim the QR was scanned, read the value
        // the backend then generates, and hand it straight back. The simulator's
        // single "SCAN QR" button is both halves.
        await PostJsonAsync(notifier + "/channel",
            new { ticket, channelValidationStatus = "channel_validation_tqr", authKey, timestamp = ts }, auth: true, ct)
            .ConfigureAwait(false);
        var cb = await GetJsonAsync($"{CodeAppRoot(u.IdentityUuid)}/channelbinding/{ticket}", auth: true, ct).ConfigureAwait(false);
        await PostJsonAsync(notifier + "/channel",
            new
            {
                ticket,
                channelValidationStatus = "channel_verify_tqr",
                channelBindingValue = cb?["channelBindingValue"]?.GetValue<string>(),
                authKey,
                timestamp = ts,
            }, auth: true, ct).ConfigureAwait(false);

        // Those calls rotate the auth-key and the signature is bound to it, so the
        // app re-pulls and re-signs before confirming. The simulator does this; we
        // do what the app does rather than find out mid-test whether it matters.
        (authKey, ts, var pull2) = await GrabAsync(u, ct).ConfigureAwait(false);
        if (pull2?["status"]?.GetValue<string>() == "OK") pull = pull2;
        var auth = await PerformAuthAsync(u, pull!, ct).ConfigureAwait(false);

        await PostJsonAsync(notifier + "/confirm",
            new
            {
                ticket = pull!["ticket"]?.GetValue<string>(),
                confirmed,
                payload = new
                {
                    response = auth["response"]?.GetValue<string>(),
                    responseSignature = auth["signedResponse"]?.GetValue<string>(),
                },
                authKey,
                timestamp = ts,
            }, auth: true, ct).ConfigureAwait(false);

        return new MitIdApproval(true, confirmed, ticket, reference);
    }

    private static MitIdReference ReferenceOf(JsonNode auth) => new(
        Decode(auth["serviceProviderName"]?.GetValue<string>()),
        Decode(auth["referenceTextHeader"]?.GetValue<string>()),
        Decode(auth["referenceTextBody"]?.GetValue<string>()));

    // The reference texts arrive base64-encoded, but a decode failure should never
    // lose the value a human is meant to read — so fall back to the raw string.
    private static string Decode(string? v)
    {
        if (string.IsNullOrEmpty(v)) return string.Empty;
        try { return Encoding.UTF8.GetString(Convert.FromBase64String(v)); }
        catch (FormatException) { return v; }
    }
}
