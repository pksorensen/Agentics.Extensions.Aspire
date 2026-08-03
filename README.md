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
| [`Agentics.Extensions.Aspire.Terminal`](src/Aspire.Hosting.Terminal/) | A terminal application served **in the browser** via `ttyd` + xterm.js, managed as an Aspire resource. Builds from local Go source, reuses an existing binary, or downloads a prebuilt CLI from the agentics.dk install store. |
| [`Agentics.Extensions.Aspire.QrCode`](src/Aspire.Hosting.QrCode/) | `WithUrlQRCode` / `WithEndpointQRCode` — a localhost page rendering a QR code for a resource URL (lazy-resolved), handy for opening a tunnelled endpoint on a phone. |
| [`Agentics.Extensions.Aspire.Testing.Videos`](src/Aspire.Hosting.Testing.Videos/) | Cinematic video-recording helpers for `Aspire.Hosting.Testing`: a Playwright walkthrough with TTS voiceover, burned-in subtitles, and ffmpeg muxing. |
| [`Agentics.Extensions.Aspire.Testkit`](src/Aspire.Hosting.Agentics.Testkit/) | One-container Agentics API, Keycloak, and hosted-Git fixture for integrator E2E tests. |
| [`Agentics.Extensions.Aspire.NextJs`](src/Aspire.Hosting.NextJs/) | Next.js/Turbopack tracing, slow-start notifications, cache limits, and a dashboard cache-reset command. |
| [`Agentics.Extensions.Aspire.MicrosoftTenant`](src/Aspire.Hosting.MicrosoftTenant/) | A local Microsoft tenant resource, seeded app registrations, and OAuth client-credentials endpoint. |
| [`Agentics.Extensions.Aspire.MicrosoftGraph`](src/Aspire.Hosting.MicrosoftGraph/) | Partial, versioned Microsoft Graph emulation for app registrations, service principals, and credentials. |
| [`Agentics.Extensions.Aspire.AzureResourceManager`](src/Aspire.Hosting.AzureResourceManager/) | Extensible ARM routing with composable, API-version-aware provider emulators. |

Each package has its own README with usage.

## Install

```bash
dotnet add package Agentics.Extensions.Aspire.Terminal
dotnet add package Agentics.Extensions.Aspire.QrCode
dotnet add package Agentics.Extensions.Aspire.Testing.Videos
dotnet add package Agentics.Extensions.Aspire.Testkit
dotnet add package Agentics.Extensions.Aspire.NextJs
dotnet add package Agentics.Extensions.Aspire.MicrosoftTenant
dotnet add package Agentics.Extensions.Aspire.MicrosoftGraph
dotnet add package Agentics.Extensions.Aspire.AzureResourceManager
```

## Versioning & release

All packages share one version (`version.txt`), bumped by
[release-please](https://github.com/googleapis/release-please) from Conventional Commits.
On release, `.github/workflows/release.yml` packs and pushes all packages to nuget.org via
**NuGet.org Trusted Publishing (OIDC)** — no stored `NUGET_API_KEY`.

## License

[MIT](LICENSE)
