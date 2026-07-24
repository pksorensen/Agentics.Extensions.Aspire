using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.QrCode;

/// <summary>
/// Adds a "scan me" QR-code link to an Aspire resource. Clicking the link in the
/// dashboard opens a small localhost page that renders a QR code for the
/// resource's endpoint (or any URL you point it at), so you can scan it straight
/// onto your phone instead of copying the URL by hand.
/// </summary>
public static class QrCodeExtensions
{
    /// <summary>
    /// Adds a QR-code link for one of the resource's own endpoints. The encoded
    /// URL has <c>localhost</c>/<c>127.0.0.1</c> rewritten to the machine's LAN
    /// IP so a phone on the same network can actually reach it.
    /// </summary>
    /// <param name="endpointName">Endpoint to encode (default <c>http</c>).</param>
    /// <param name="displayText">Dashboard link text (default "📱 QR code").</param>
    public static IResourceBuilder<T> WithEndpointQRCode<T>(
        this IResourceBuilder<T> builder,
        string endpointName = "http",
        string? displayText = null)
        where T : IResourceWithEndpoints
    {
        var endpoint = builder.GetEndpoint(endpointName);
        var label = displayText ?? "📱 QR code";

        return AddQrUrl(builder, $"{builder.Resource.Name}-{endpointName}", label, _ =>
        {
            var url = endpoint.IsAllocated ? endpoint.Url : null;
            return ValueTask.FromResult(RewriteToLan(url));
        });
    }

    /// <summary>
    /// Adds a QR-code link for an arbitrary fixed URL — e.g. a tunnel's public
    /// URL that the phone should hit (<c>https://coach--…tunnels.agentics.dk:8443</c>).
    /// </summary>
    public static IResourceBuilder<T> WithUrlQRCode<T>(
        this IResourceBuilder<T> builder,
        string url,
        string? displayText = null)
        where T : IResource
    {
        var label = displayText ?? "📱 QR code";
        return AddQrUrl(builder, $"{builder.Resource.Name}-url", label, _ => ValueTask.FromResult<string?>(url));
    }

    /// <summary>
    /// Adds a QR-code link for a URL produced by a <see cref="ReferenceExpression"/>
    /// (e.g. a tunnel <c>GetEndpoint(...)</c> result). The expression is resolved
    /// lazily at request time, so links that only materialise once the tunnel is
    /// up still work.
    /// </summary>
    public static IResourceBuilder<T> WithUrlQRCode<T>(
        this IResourceBuilder<T> builder,
        ReferenceExpression url,
        string? displayText = null)
        where T : IResource
    {
        var label = displayText ?? "📱 QR code";
        return AddQrUrl(builder, $"{builder.Resource.Name}-expr", label, async ct =>
        {
            // Bound the wait so a not-yet-ready expression (e.g. a tunnel still
            // coming up) renders the page's "retry" state instead of hanging the
            // request until the value resolves.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            try { return await url.GetValueAsync(timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
        });
    }

    private static IResourceBuilder<T> AddQrUrl<T>(
        IResourceBuilder<T> builder,
        string token,
        string label,
        Func<CancellationToken, ValueTask<string?>> resolver)
        where T : IResource
    {
        var server = GetOrAddServer(builder.ApplicationBuilder);
        var qrLink = server.Register(Sanitize(token), label, resolver);
        return builder.WithUrl(qrLink, label);
    }

    private static QrCodeServer GetOrAddServer(IDistributedApplicationBuilder builder)
    {
        var existing = builder.Services
            .FirstOrDefault(d => d.ServiceType == typeof(QrCodeServer))?
            .ImplementationInstance as QrCodeServer;
        if (existing is not null) return existing;

        var server = new QrCodeServer();
        server.Start();
        // Registered as a singleton instance so DI disposes it (stops the
        // listener) when the AppHost shuts down.
        builder.Services.AddSingleton(server);
        return server;
    }

    private static string Sanitize(string token)
    {
        Span<char> buf = stackalloc char[token.Length];
        for (var i = 0; i < token.Length; i++)
            buf[i] = char.IsLetterOrDigit(token[i]) || token[i] is '-' or '_' ? token[i] : '-';
        return new string(buf);
    }

    /// <summary>
    /// Rewrites a loopback host to the machine's primary LAN IPv4 so the encoded
    /// URL is reachable from another device on the network. Non-loopback hosts
    /// are returned unchanged.
    /// </summary>
    private static string? RewriteToLan(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return url;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return url;
        if (!uri.IsLoopback) return url;

        var lan = GetLanIp();
        if (lan is null) return url;

        return new UriBuilder(uri) { Host = lan }.Uri.ToString();
    }

    private static string? GetLanIp()
    {
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                    continue;

                foreach (var addr in ni.GetIPProperties().UnicastAddresses)
                {
                    if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    if (IPAddress.IsLoopback(addr.Address)) continue;
                    return addr.Address.ToString();
                }
            }
        }
        catch { /* fall through */ }
        return null;
    }
}
