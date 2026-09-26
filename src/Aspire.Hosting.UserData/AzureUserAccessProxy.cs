using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text.Json;

using Azure.Core;

namespace Aspire.Hosting.UserData;

/// <summary>
/// A loopback-only development gateway that replaces an emulator bearer token with an access
/// token from the Azure user who explicitly requested a production snapshot. Real tokens remain
/// in the AppHost process and only allowlisted upstream routes can be reached.
/// </summary>
public sealed class AzureUserAccessProxy : IDisposable
{
    private static readonly HashSet<string> ForwardedRequestHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Accept",
        "Accept-Encoding",
        "anthropic-beta",
        "anthropic-version",
        "Content-Language",
        "OpenAI-Beta",
        "User-Agent",
        "baggage",
        "traceparent",
        "tracestate",
        "x-ms-client-request-id",
        "x-ms-useragent",
    };

    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.All,
    });

    private readonly HttpListener _listener = new();
    private readonly IReadOnlyList<AzureUserAccessProxyRoute> _routes;
    private readonly Uri _emulatorUserInfoEndpoint;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _server;
    private volatile TokenCredential? _credential;
    private string? _displayName;

    public AzureUserAccessProxy(
        Uri emulatorUserInfoEndpoint,
        IEnumerable<AzureUserAccessProxyRoute> routes)
    {
        ArgumentNullException.ThrowIfNull(emulatorUserInfoEndpoint);
        ArgumentNullException.ThrowIfNull(routes);
        if (!emulatorUserInfoEndpoint.IsLoopback)
            throw new ArgumentException("The emulator validation endpoint must be loopback-only.", nameof(emulatorUserInfoEndpoint));

        _routes = routes.OrderByDescending(route => route.PathPrefix.Length).ToArray();
        if (_routes.Count == 0) throw new ArgumentException("At least one proxy route is required.", nameof(routes));
        if (_routes.Any(route =>
            !route.PathPrefix.StartsWith('/') ||
            !route.PathPrefix.EndsWith('/') ||
            !route.UpstreamBaseUrl.IsAbsoluteUri ||
            route.UpstreamBaseUrl.Scheme != Uri.UriSchemeHttps ||
            !route.UpstreamBaseUrl.AbsolutePath.EndsWith('/')))
        {
            throw new ArgumentException(
                "Proxy routes require slash-delimited path prefixes and absolute HTTPS upstream base URLs ending in a slash.",
                nameof(routes));
        }

        _emulatorUserInfoEndpoint = emulatorUserInfoEndpoint;
        var port = FreeLoopbackPort();
        BaseAddress = new Uri($"http://127.0.0.1:{port}/", UriKind.Absolute);
        _listener.Prefixes.Add(BaseAddress.ToString());
        _listener.Start();
        _server = ServeAsync(_stopping.Token);
    }

    public Uri BaseAddress { get; }

    /// <summary>The upstream identity currently held by the proxy, for diagnostics only.</summary>
    public string? DisplayName => _displayName;

    internal void UseCredential(TokenCredential credential, string displayName)
    {
        ArgumentNullException.ThrowIfNull(credential);
        _credential = credential;
        _displayName = displayName;
    }

    private async Task ServeAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (HttpListenerException) when (ct.IsCancellationRequested || !_listener.IsListening)
            {
                return;
            }

            _ = HandleAsync(context, ct);
        }
    }

    private async Task HandleAsync(HttpListenerContext context, CancellationToken ct)
    {
        try
        {
            if (context.Request.Url?.AbsolutePath == "/healthz")
            {
                await WriteJsonAsync(context.Response, _credential is null ? 503 : 200, new
                {
                    status = _credential is null ? "waiting-for-azure-user" : "ready",
                    identity = _displayName,
                }, ct).ConfigureAwait(false);
                return;
            }

            var bearer = Bearer(context.Request.Headers["Authorization"]);
            if (bearer is null || !await IsEmulatorTokenAsync(bearer, ct).ConfigureAwait(false))
            {
                await WriteJsonAsync(context.Response, 401, new { error = "invalid_local_token" }, ct)
                    .ConfigureAwait(false);
                return;
            }

            var credential = _credential;
            if (credential is null)
            {
                await WriteJsonAsync(context.Response, 503, new { error = "azure_user_not_connected" }, ct)
                    .ConfigureAwait(false);
                return;
            }

            var path = context.Request.Url?.AbsolutePath ?? "/";
            var route = _routes.FirstOrDefault(candidate =>
                path.StartsWith(candidate.PathPrefix, StringComparison.OrdinalIgnoreCase));
            if (route is null)
            {
                await WriteJsonAsync(context.Response, 404, new { error = "route_not_allowed" }, ct)
                    .ConfigureAwait(false);
                return;
            }

            var suffix = path[route.PathPrefix.Length..].TrimStart('/');
            var target = new Uri(route.UpstreamBaseUrl, suffix + (context.Request.Url?.Query ?? string.Empty));
            if (!target.AbsolutePath.StartsWith(route.UpstreamBaseUrl.AbsolutePath, StringComparison.Ordinal))
            {
                await WriteJsonAsync(context.Response, 404, new { error = "route_not_allowed" }, ct)
                    .ConfigureAwait(false);
                return;
            }

            var accessToken = await credential.GetTokenAsync(new TokenRequestContext([route.Scope]), ct)
                .ConfigureAwait(false);
            using var request = new HttpRequestMessage(new HttpMethod(context.Request.HttpMethod), target);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.Token);

            foreach (var key in context.Request.Headers.AllKeys)
            {
                if (key is null || !ForwardedRequestHeaders.Contains(key)) continue;
                var values = context.Request.Headers.GetValues(key);
                if (values is not null) request.Headers.TryAddWithoutValidation(key, values);
            }

            if (context.Request.HasEntityBody)
            {
                request.Content = new StreamContent(context.Request.InputStream);
                if (context.Request.ContentLength64 >= 0)
                    request.Content.Headers.ContentLength = context.Request.ContentLength64;
                if (!string.IsNullOrWhiteSpace(context.Request.ContentType))
                    request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(context.Request.ContentType);
            }

            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            context.Response.StatusCode = (int)response.StatusCode;
            CopyHeaders(response.Headers, context.Response);
            CopyHeaders(response.Content.Headers, context.Response);
            await response.Content.CopyToAsync(context.Response.OutputStream, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            context.Response.Abort();
        }
        catch (Exception error)
        {
            try
            {
                if (!context.Response.OutputStream.CanWrite) return;
                await WriteJsonAsync(context.Response, 502, new
                {
                    error = "azure_proxy_failed",
                    message = error.Message.Split('\n')[0],
                }, CancellationToken.None).ConfigureAwait(false);
            }
            catch (HttpListenerException)
            {
                // The upstream may fail after response headers or part of a stream have already
                // reached the caller. At that point the only safe action is to close the response.
            }
            catch (InvalidOperationException)
            {
                // HttpListener forbids replacing a response after headers have been committed.
            }
        }
        finally
        {
            context.Response.Close();
        }
    }

    private async Task<bool> IsEmulatorTokenAsync(string bearer, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, _emulatorUserInfoEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        return response.IsSuccessStatusCode;
    }

    private static void CopyHeaders(HttpHeaders source, HttpListenerResponse destination)
    {
        foreach (var header in source)
        {
            if (IsHopByHop(header.Key) ||
                header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) ||
                header.Key.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase)) continue;
            try { destination.Headers[header.Key] = string.Join(",", header.Value); }
            catch (ArgumentException) { /* HttpListener owns restricted response headers. */ }
        }
    }

    private static bool IsHopByHop(string name) => name.Equals("Connection", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Keep-Alive", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Proxy-Authenticate", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase)
        || name.Equals("TE", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Trailer", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Upgrade", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Host", StringComparison.OrdinalIgnoreCase);

    private static string? Bearer(string? header) =>
        header?.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) == true
            ? header[7..].Trim()
            : null;

    private static async Task WriteJsonAsync(HttpListenerResponse response, int status, object value, CancellationToken ct)
    {
        response.StatusCode = status;
        response.ContentType = "application/json; charset=utf-8";
        await JsonSerializer.SerializeAsync(response.OutputStream, value, cancellationToken: ct).ConfigureAwait(false);
    }

    private static int FreeLoopbackPort()
    {
        using var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        return ((IPEndPoint)socket.LocalEndpoint).Port;
    }

    public void Dispose()
    {
        _stopping.Cancel();
        _listener.Close();
        _stopping.Dispose();
        GC.SuppressFinalize(this);
    }
}

public sealed record AzureUserAccessProxyRoute(string PathPrefix, Uri UpstreamBaseUrl, string Scope);
