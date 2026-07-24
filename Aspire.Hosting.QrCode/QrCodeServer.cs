using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using QRCoder;

namespace Aspire.Hosting.QrCode;

/// <summary>
/// A tiny localhost HTTP server that renders QR-code pages on demand. One
/// instance per AppHost (registered as a DI singleton so it's disposed on
/// shutdown). Each <c>WithEndpointQRCode</c> / <c>WithUrlQRCode</c> call
/// registers a token whose target URL is resolved lazily at request time —
/// so tunnel URLs that only materialise once the tunnel is up still work.
///
/// The QR page is only ever opened on the dev machine (from the Aspire
/// dashboard link); the phone just scans the code, which encodes the *target*
/// URL. That's why binding to 127.0.0.1 is enough.
/// </summary>
internal sealed class QrCodeServer : IAsyncDisposable
{
    private readonly HttpListener _listener = new();
    private readonly ConcurrentDictionary<string, Entry> _entries = new();
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    public int Port { get; }

    public QrCodeServer()
    {
        Port = GetFreePort();
        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
    }

    public void Start()
    {
        _listener.Start();
        _loop = Task.Run(AcceptLoopAsync);
    }

    /// <summary>
    /// Registers a QR target. <paramref name="resolver"/> is invoked per request,
    /// so the URL can be discovered late (e.g. a tunnel slot URL). Returns the
    /// dashboard link that renders the QR page for this token.
    /// </summary>
    public string Register(string token, string label, Func<CancellationToken, ValueTask<string?>> resolver)
    {
        _entries[token] = new Entry(label, resolver);
        return $"http://127.0.0.1:{Port}/qr/{token}";
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync().ConfigureAwait(false); }
            catch when (_cts.IsCancellationRequested) { break; }
            catch { continue; }

            _ = Task.Run(() => HandleAsync(ctx));
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        try
        {
            var path = ctx.Request.Url?.AbsolutePath ?? "/";
            // /qr/{token}        → HTML page
            // /qr/{token}/png    → raw PNG (used by the <img> on the page)
            var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length < 2 || segments[0] != "qr")
            {
                ctx.Response.StatusCode = 404;
                ctx.Response.Close();
                return;
            }

            var token = segments[1];
            var wantsPng = segments.Length >= 3 && segments[2] == "png";

            if (!_entries.TryGetValue(token, out var entry))
            {
                await WriteTextAsync(ctx, 404, "Unknown QR token").ConfigureAwait(false);
                return;
            }

            var url = await entry.Resolver(_cts.Token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(url))
            {
                await WritePageAsync(ctx, entry.Label, url: null, pngToken: token).ConfigureAwait(false);
                return;
            }

            if (wantsPng)
            {
                var png = GeneratePng(url);
                ctx.Response.StatusCode = 200;
                ctx.Response.ContentType = "image/png";
                ctx.Response.Headers["Cache-Control"] = "no-store";
                await ctx.Response.OutputStream.WriteAsync(png).ConfigureAwait(false);
                ctx.Response.Close();
                return;
            }

            await WritePageAsync(ctx, entry.Label, url, pngToken: token).ConfigureAwait(false);
        }
        catch
        {
            try { ctx.Response.Abort(); } catch { /* ignore */ }
        }
    }

    private static byte[] GeneratePng(string url)
    {
        using var gen = new QRCodeGenerator();
        using var data = gen.CreateQrCode(url, QRCodeGenerator.ECCLevel.M);
        return new PngByteQRCode(data).GetGraphic(12);
    }

    private static async Task WritePageAsync(HttpListenerContext ctx, string label, string? url, string pngToken)
    {
        var safeLabel = WebUtility.HtmlEncode(label);
        string body;
        if (url is null)
        {
            body = $"""
                <div class="card">
                  <h1>{safeLabel}</h1>
                  <p class="muted">This URL isn't available yet — the resource (or its tunnel)
                  may still be starting. Refresh in a few seconds.</p>
                  <p><a href="" onclick="location.reload();return false;">↻ Retry</a></p>
                </div>
                """;
        }
        else
        {
            var safeUrl = WebUtility.HtmlEncode(url);
            body = $$"""
                <div class="card">
                  <h1>{{safeLabel}}</h1>
                  <img class="qr" src="/qr/{{pngToken}}/png" alt="QR code" width="320" height="320" />
                  <a class="url" href="{{safeUrl}}" target="_blank" rel="noopener">{{safeUrl}}</a>
                  <button class="copy" onclick="navigator.clipboard.writeText('{{safeUrl}}').then(()=>{this.textContent='Copied ✓'})">Copy URL</button>
                </div>
                """;
        }

        var html = $$"""
            <!doctype html>
            <html lang="en">
            <head>
              <meta charset="utf-8" />
              <meta name="viewport" content="width=device-width, initial-scale=1" />
              <title>{{safeLabel}} — QR</title>
              <style>
                :root { color-scheme: dark; }
                * { box-sizing: border-box; }
                body {
                  margin: 0; min-height: 100vh; display: grid; place-items: center;
                  background: #0d0f12; color: #e9eaec;
                  font: 16px/1.5 ui-sans-serif, system-ui, -apple-system, "Segoe UI", sans-serif;
                }
                .card {
                  background: #16191e; border: 1px solid #262b33; border-radius: 18px;
                  padding: 32px 36px; text-align: center; max-width: 420px; width: calc(100% - 32px);
                  box-shadow: 0 12px 40px rgba(0,0,0,.45);
                }
                h1 { margin: 0 0 20px; font-size: 18px; font-weight: 600; }
                .qr {
                  background: #fff; border-radius: 14px; padding: 14px;
                  width: 320px; height: 320px; max-width: 100%; aspect-ratio: 1;
                }
                .url {
                  display: block; margin: 20px 0 4px; word-break: break-all;
                  color: #f87f2e; text-decoration: none; font-size: 14px;
                }
                .url:hover { text-decoration: underline; }
                .muted { color: #8b9099; font-size: 14px; }
                .copy {
                  margin-top: 14px; cursor: pointer; border: 1px solid #333a44;
                  background: #1f242c; color: #e9eaec; border-radius: 9px;
                  padding: 8px 16px; font-size: 13px;
                }
                .copy:hover { background: #262c35; }
                a { color: #f87f2e; }
              </style>
            </head>
            <body>{{body}}</body>
            </html>
            """;

        var bytes = Encoding.UTF8.GetBytes(html);
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "text/html; charset=utf-8";
        ctx.Response.Headers["Cache-Control"] = "no-store";
        await ctx.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        ctx.Response.Close();
    }

    private static async Task WriteTextAsync(HttpListenerContext ctx, int status, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "text/plain; charset=utf-8";
        await ctx.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        ctx.Response.Close();
    }

    private static int GetFreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { _listener.Stop(); _listener.Close(); } catch { /* ignore */ }
        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); } catch { /* ignore */ }
        }
        _cts.Dispose();
    }

    private sealed record Entry(string Label, Func<CancellationToken, ValueTask<string?>> Resolver);
}
