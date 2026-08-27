using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;

namespace Agentics.MitID.Testing;

/// <summary>
/// Talks to an <c>agent-mitid</c> service (for example
/// <c>https://mitid.agentics.dk</c>) instead of driving pp.mitid.dk directly.
/// </summary>
/// <remarks>
/// The difference that matters is the registry: here a test names a user the way
/// a person does — project plus bruger-ID — and the service resolves the identity
/// UUID, the authenticator and the PIN. So the test survives someone recreating
/// the user, and the same named users are shared across suites and machines.
/// The cost is a bearer token in CI and a service that has to be up.
/// </remarks>
public sealed class AgentMitIdClient
{
    private readonly HttpClient _http;
    private readonly Uri _baseUrl;
    private readonly string _token;

    /// <summary>Creates a client against a running agent-mitid instance.</summary>
    /// <param name="baseUrl">The service origin, e.g. <c>https://mitid.agentics.dk</c>.</param>
    /// <param name="token">The MCP bearer token, from the PWA's Indstillinger tab.</param>
    /// <param name="http">An optional client to borrow.</param>
    public AgentMitIdClient(Uri baseUrl, string token, HttpClient? http = null)
    {
        _baseUrl = baseUrl;
        _token = token;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
    }

    private async Task<JsonNode?> SendAsync(HttpMethod method, string path, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, new Uri(_baseUrl, path));
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        using var res = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var body = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (res.StatusCode == HttpStatusCode.Unauthorized)
            throw new MitIdException($"agent-mitid refused the token — check MITID_MCP_TOKEN ({_baseUrl})");
        if (!res.IsSuccessStatusCode)
            throw new MitIdException($"agent-mitid {method} {path}: {(int)res.StatusCode} {body.Trim()}");
        return string.IsNullOrWhiteSpace(body) ? null : JsonNode.Parse(body);
    }

    /// <summary>Resolves a registered user by bruger-ID within a project.</summary>
    public async Task<MitIdTestUser> FindAsync(string project, string userId, CancellationToken ct = default)
    {
        var list = await SendAsync(HttpMethod.Get, $"/api/projects/{Uri.EscapeDataString(project)}/users", ct)
            .ConfigureAwait(false);
        foreach (var u in list?["users"]?.AsArray() ?? [])
        {
            if (!string.Equals(u?["userId"]?.GetValue<string>(), userId, StringComparison.OrdinalIgnoreCase)) continue;
            return new MitIdTestUser(
                userId,
                u!["identityUuid"]!.GetValue<string>(),
                u["authenticatorId"]?.GetValue<string>() ?? string.Empty,
                u["pin"]?.GetValue<string>() ?? MitIdTestEnvironment.DefaultPin,
                u["name"]?.GetValue<string>());
        }
        throw new MitIdException(
            $"no user \"{userId}\" registered under project \"{project}\" — register it once with mitid_user_adopt, " +
            "or create it with mitid_user_create");
    }

    /// <summary>Approves the pending login for a registered user.</summary>
    public Task<MitIdApproval> ApproveAsync(string project, string userId, TimeSpan wait = default, CancellationToken ct = default)
        => RespondAsync(project, userId, wait, "approve", ct);

    /// <summary>Refuses the pending login for a registered user.</summary>
    public Task<MitIdApproval> RejectAsync(string project, string userId, TimeSpan wait = default, CancellationToken ct = default)
        => RespondAsync(project, userId, wait, "reject", ct);

    private async Task<MitIdApproval> RespondAsync(string project, string userId, TimeSpan wait, string verb, CancellationToken ct)
    {
        var user = await FindAsync(project, userId, ct).ConfigureAwait(false);
        var seconds = (int)Math.Clamp(wait.TotalSeconds, 0, 60);
        var path = $"/api/projects/{Uri.EscapeDataString(project)}/users/{user.IdentityUuid}/{verb}?waitSeconds={seconds}";
        var res = await SendAsync(HttpMethod.Post, path, ct).ConfigureAwait(false);
        var r = res?["reference"];
        return new MitIdApproval(
            res?["answered"]?.GetValue<bool>() ?? false,
            verb == "approve",
            res?["ticket"]?.GetValue<string>() ?? string.Empty,
            new MitIdReference(
                r?["serviceProvider"]?.GetValue<string>() ?? string.Empty,
                r?["header"]?.GetValue<string>() ?? string.Empty,
                r?["body"]?.GetValue<string>() ?? string.Empty));
    }

    /// <summary>
    /// Opens a time-boxed window in which the service approves this user's logins
    /// by itself — for a login triggered somewhere the test cannot call approve.
    /// Pass <see cref="TimeSpan.Zero"/> to close it.
    /// </summary>
    public async Task AutoApproveAsync(string project, string userId, TimeSpan window, CancellationToken ct = default)
    {
        var user = await FindAsync(project, userId, ct).ConfigureAwait(false);
        var path = $"/api/projects/{Uri.EscapeDataString(project)}/users/{user.IdentityUuid}/autoapprove";
        if (window <= TimeSpan.Zero)
        {
            await SendAsync(HttpMethod.Delete, path, ct).ConfigureAwait(false);
            return;
        }
        using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(_baseUrl, path))
        {
            Content = new StringContent($"{{\"minutes\":{Math.Max(1, (int)window.TotalMinutes)}}}",
                System.Text.Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        using var res = await _http.SendAsync(req, ct).ConfigureAwait(false);
        if (!res.IsSuccessStatusCode)
            throw new MitIdException($"agent-mitid autoapprove: {(int)res.StatusCode}");
    }
}
