# Agentics.Extensions.Aspire.Terminal

An [.NET Aspire](https://learn.microsoft.com/dotnet/aspire/) hosting integration that runs a
terminal application **in the browser** via [`ttyd`](https://github.com/tsl0922/ttyd) + xterm.js,
managed as a first-class Aspire resource (start/stop/restart, logs, health, clickable dashboard URL).

> Published as `Agentics.Extensions.Aspire.Terminal`; the API lives in the `Aspire.Hosting`
> namespace, so a single `using Aspire.Hosting;` (implicit in an AppHost) exposes it.

## Install

```bash
dotnet add package Agentics.Extensions.Aspire.Terminal
```

## Usage

Build a local Go source tree (dev):

```csharp
var terminal = builder.AddTerminalApp(
    "vibecast-terminal",
    projectDir: "../../external/vibecast",
    outputBinary: "vibecast");
```

Or provision a prebuilt CLI from the [agentics.dk](https://agentics.dk) install store — no Go
toolchain, no source checkout required:

```csharp
var terminal = builder.AddTerminalApp(
    "vibecast-terminal",
    agenticsComponent: "vibecast");   // downloads + sha256-verifies the platform binary
```

### Binary resolution order

1. **Go + source present** → `go build` (dev; rebuilt every start).
2. An existing prebuilt binary at the target path → used as-is.
3. `agenticsComponent` set → downloaded from `https://agentics.dk/install/<component>/download`
   (checksum-verified against the release's `*_checksums.txt`). Override the host with the
   `AGENTICS_BASE_URL` environment variable or the `agenticsBaseUrl` parameter; pin with `version`.

## Requirements

- `ttyd` on the host (`apt install ttyd`, `brew install ttyd`, …).
- Go **only** when building from source (resolution path 1).

## License

MIT
