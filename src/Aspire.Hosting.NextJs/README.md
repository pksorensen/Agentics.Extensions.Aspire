# Agentics.Extensions.Aspire.NextJs

Local-development diagnostics and dashboard actions for Next.js and Turbopack.
The extension works with both `AddNodeApp` and `AddNextJsApp` resources.

```bash
dotnet add package Agentics.Extensions.Aspire.NextJs
```

```csharp
var web = builder.AddNodeApp("web", "../web", "unused")
    .WithNpm(install: true)
    .WithRunScript("dev")
    .DisableTelemetry()
    .WithTurbopackTracing()
    .ClearTurbopackCache(limit: "1gb")
    .WithSlowStartDetector(options =>
    {
        options.SlowCompilationThreshold = TimeSpan.FromSeconds(10);
        options.CacheWarningLimit = "1gb";
    });
```

## What the extensions do

- `DisableTelemetry()` sets `NEXT_TELEMETRY_DISABLED=1`. Besides opting out of
  anonymous usage reporting, it stops Next.js from spawning `detached-flush.js`
  on exit. That detached child uploads the telemetry batch so the command need
  not wait on the network — but when the upload can neither complete nor fail
  (no egress, a black-holed connection) it spins at ~100% CPU indefinitely, and
  every restart adds another one. Apply it to every Next.js resource.
- `WithTurbopackTracing()` sets `NEXT_TURBOPACK_TRACING=1`. Next writes the
  detailed binary trace to `.next/dev/trace-turbopack`; open it with
  `npx next internal trace .next/dev/trace-turbopack`.
- `ClearTurbopackCache(limit: "1gb")` adds a highlighted **Clear Turbopack
  cache** resource command. It also clears the generated cache before AppHost
  startup when it has reached the limit. The dashboard command stops Next.js,
  clears the cache, and starts the resource again.
- `WithSlowStartDetector()` watches Next's lightweight `.next/dev/trace` for
  completed `compile-path` and `ensure-page` spans. A slow compile or oversized
  cache writes a warning to the resource log, adds diagnostic resource
  properties, and sends an Aspire notification linking to the resource command.

The detector deliberately reads the lightweight JSON trace rather than parsing
`trace-turbopack`: the latter is a large binary diagnostic intended for the Next
trace viewer. Detailed tracing remains opt-in because even a short session can
produce more than 100 MB of trace data.

Both cache layouts are supported:

- Next.js 16: `.next/dev/cache/turbopack`
- Older Next.js versions: `.next/cache/turbopack`

All cache content is generated and can be recreated by Next.js.
