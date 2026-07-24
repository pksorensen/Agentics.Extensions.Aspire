# Agentics.Extensions.Aspire

Reusable [.NET Aspire](https://learn.microsoft.com/dotnet/aspire/) **hosting** extensions
used across the Agentics platform, published to NuGet so any AppHost can consume them.

> **Naming:** `Aspire.*` is a reserved package-ID prefix owned by Microsoft on nuget.org,
> so these publish as `Agentics.Extensions.Aspire.*` while keeping the ergonomic
> `namespace Aspire.Hosting;` in code (the same split CommunityToolkit.Aspire uses).
> Your `using Aspire.Hosting;` is unaffected.

## Packages

| Package | What it adds |
| --- | --- |
| [`Agentics.Extensions.Aspire.Terminal`](Aspire.Hosting.Terminal/) | A terminal application served **in the browser** via `ttyd` + xterm.js, managed as an Aspire resource. Builds from local Go source, reuses an existing binary, or downloads a prebuilt CLI from the agentics.dk install store. |
| [`Agentics.Extensions.Aspire.QrCode`](Aspire.Hosting.QrCode/) | `WithUrlQRCode` / `WithEndpointQRCode` — a localhost page rendering a QR code for a resource URL (lazy-resolved), handy for opening a tunnelled endpoint on a phone. |
| [`Agentics.Extensions.Aspire.Testing.Videos`](Aspire.Hosting.Testing.Videos/) | Cinematic video-recording helpers for `Aspire.Hosting.Testing`: a Playwright walkthrough with TTS voiceover, burned-in subtitles, and ffmpeg muxing. |

Each package has its own README with usage.

## Install

```bash
dotnet add package Agentics.Extensions.Aspire.Terminal
dotnet add package Agentics.Extensions.Aspire.QrCode
dotnet add package Agentics.Extensions.Aspire.Testing.Videos
```

## Versioning & release

All three packages share one version (`version.txt`), bumped by
[release-please](https://github.com/googleapis/release-please) from Conventional Commits.
On release, `.github/workflows/release.yml` packs and pushes all three to nuget.org via
**NuGet.org Trusted Publishing (OIDC)** — no stored `NUGET_API_KEY`.

## License

[MIT](LICENSE)
