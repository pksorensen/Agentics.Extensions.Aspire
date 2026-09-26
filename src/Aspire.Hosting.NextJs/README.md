# Agentics.Extensions.Aspire.NextJs

Local-development diagnostics and dashboard actions for Next.js and Turbopack.
The extension works with both `AddNodeApp` and `AddNextJsApp` resources.

```bash
dotnet add package Agentics.Extensions.Aspire.NextJs
```

```csharp
var web = builder.AddNodeApp("web", "../web", "unused")
    .WithNextJsDefaults()
    .WithNpm(install: true)
    .WithRunScript("dev")
    .WithClearUserDataCommand("../.dev-user-data")
    .WithClearNextBuildCommand("../web", ".next", ".next-e2e")
    .WithTurbopackTracing()
    .ClearTurbopackCache(limit: "1gb")
    .WithSlowStartDetector(options =>
    {
        options.SlowCompilationThreshold = TimeSpan.FromSeconds(10);
        options.CacheWarningLimit = "1gb";
    });
```

## What the extensions do

- `WithNextJsDefaults()` applies the defaults every Next.js development resource
  should share: a fixed 16 GiB V8 old-space limit, `NEXT_DISABLE_MEM_OVERRIDE=1`
  so Next.js never falls back to half of host RAM, and telemetry disabled. Pass
  `maxOldSpaceSizeMegabytes` to override the heap limit for an exceptional app.
- `DisableTelemetry()` sets `NEXT_TELEMETRY_DISABLED=1`. Besides opting out of
  anonymous usage reporting, it stops Next.js from spawning `detached-flush.js`
  on exit. That detached child uploads the telemetry batch so the command need
  not wait on the network — but when the upload can neither complete nor fail
  (no egress, a black-holed connection) it spins at ~100% CPU indefinitely, and
  every restart adds another one. Apply it to every Next.js resource.
- `WithTurbopackTracing()` sets `NEXT_TURBOPACK_TRACING=1`. Next writes the
  detailed binary trace to `.next/dev/trace-turbopack`; open it with
  `npx next internal trace .next/dev/trace-turbopack`.
- `WithClearUserDataCommand()` adds a local-only **Clear User Data** command.
  It stops Next.js, moves the current directory to a timestamped backup,
  creates an empty directory, and starts Next.js again.
- `WithClearNextBuildCommand()` adds a local-only **Clear .next & Restart**
  command. It stops Next.js, removes the named generated `.next*` directories,
  and starts Next.js again. Directory names must be direct children of the
  supplied working directory.
- `ClearTurbopackCache(limit: "1gb")` adds a highlighted **Clear Turbopack
  cache** resource command. It also clears the generated cache before AppHost
  startup when it has reached the limit. The dashboard command stops Next.js,
  clears the cache, and starts the resource again.
- `WithSlowStartDetector()` watches Next's lightweight `.next/dev/trace` for
  completed `compile-path` and `ensure-page` spans. A slow compile or oversized
  cache writes a warning to the resource log, adds diagnostic resource
  properties, and sends an Aspire notification linking to the resource command.

The detector reads the trace **incrementally**. Next never truncates
`.next/dev/trace` — it appends across `next dev` restarts and keeps weeks of
history — so the detector starts at the end of the file and each poll reads only
the bytes appended since the last one, through a single reused 64 KB window that
stays below the large-object-heap threshold. A poll that lands mid-write leaves
the partial trailing batch unconsumed and picks it up next time.

This matters for long-lived AppHosts. Reading the whole file on every change
costs O(file) per append into a fresh, ever-larger array; on a two-day session
that is gigabytes of large-object allocations whose holes the GC sweeps but does
not compact. Measured on a real 7.2 MB / 600-batch trace: 7.2 MB consumed
incrementally against ~2.1 GB re-read the old way.

The cache measurement is separate and far coarser (`CacheSizePollInterval`,
5 minutes by default) because it walks the generated cache tree. A slow-compile
warning reports the most recent measurement rather than walking it again.

The detector deliberately reads the lightweight JSON trace rather than parsing
`trace-turbopack`: the latter is a large binary diagnostic intended for the Next
trace viewer. Detailed tracing remains opt-in because even a short session can
produce more than 100 MB of trace data.

Both cache layouts are supported:

- Next.js 16: `.next/dev/cache/turbopack`
- Older Next.js versions: `.next/cache/turbopack`

All cache content is generated and can be recreated by Next.js.
