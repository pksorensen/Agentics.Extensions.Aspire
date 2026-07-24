# src/

Source for the three published packages. Each folder is one NuGet package
(`namespace Aspire.Hosting;`, PackageId `Agentics.Extensions.Aspire.*`):

| Folder | Package |
| --- | --- |
| `Aspire.Hosting.Terminal/` | `Agentics.Extensions.Aspire.Terminal` |
| `Aspire.Hosting.QrCode/` | `Agentics.Extensions.Aspire.QrCode` |
| `Aspire.Hosting.Testing.Videos/` | `Agentics.Extensions.Aspire.Testing.Videos` |

Shared pack metadata lives in `Directory.Build.props`; the version is `version.txt`
(bumped by release-please). See the [repo README](../README.md) for install + release.
