# Agentics.Extensions.Aspire.QrCode

An [.NET Aspire](https://learn.microsoft.com/dotnet/aspire/) hosting integration that adds a
clickable **"📱 scan me" QR-code link** to a resource in the Aspire dashboard. Clicking it opens a
tiny localhost page that renders a QR code (via [QRCoder](https://github.com/codebude/QRCoder),
no System.Drawing) for the target URL — scan it onto your phone.

> Published as `Agentics.Extensions.Aspire.QrCode`; the API lives in the `Aspire.Hosting`
> namespace, so a single `using Aspire.Hosting;` (implicit in an AppHost) exposes it.

## Install

```bash
dotnet add package Agentics.Extensions.Aspire.QrCode
```

## Usage

```csharp
// QR for the resource's own endpoint (loopback is rewritten to the LAN IPv4
// so a phone on the same wifi can reach it):
builder.AddProject<Projects.Web>("web")
       .WithEndpointQRCode("http", "📱 Scan to open on phone");

// QR for a fixed URL:
someResource.WithUrlQRCode("https://example.com");

// QR for a lazily-resolved URL (e.g. a tunnel endpoint that only materializes at runtime):
someResource.WithUrlQRCode(tunnel.GetEndpoint("public"), "📱 Scan to open on phone");
```

Targets are resolved **per request**, so tunnel URLs that only exist once the tunnel is up work
correctly. The QR page binds to `127.0.0.1` — only the dev machine views it; the phone just scans
the encoded URL.

## License

MIT
