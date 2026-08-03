# src/

Source for the published packages. Each folder is one NuGet package
(`namespace Aspire.Hosting;`, PackageId `Agentics.Extensions.Aspire.*`):

| Folder | Package |
| --- | --- |
| `Aspire.Hosting.Terminal/` | `Agentics.Extensions.Aspire.Terminal` |
| `Aspire.Hosting.QrCode/` | `Agentics.Extensions.Aspire.QrCode` |
| `Aspire.Hosting.Testing.Videos/` | `Agentics.Extensions.Aspire.Testing.Videos` |
| `Aspire.Hosting.Agentics.Testkit/` | `Agentics.Extensions.Aspire.Testkit` |
| `Aspire.Hosting.NextJs/` | `Agentics.Extensions.Aspire.NextJs` |
| `Aspire.Hosting.MicrosoftTenant/` | `Agentics.Extensions.Aspire.MicrosoftTenant` |
| `Aspire.Hosting.MicrosoftGraph/` | `Agentics.Extensions.Aspire.MicrosoftGraph` |
| `Aspire.Hosting.AzureResourceManager/` | `Agentics.Extensions.Aspire.AzureResourceManager` |

`MicrosoftTenant.Emulator/` is the reference emulator host used by the three
Microsoft/Azure packages. It is published as a container, not a NuGet package.

Shared pack metadata lives in `Directory.Build.props`; the version is `version.txt`
(bumped by release-please). See the [repo README](../README.md) for install + release.
