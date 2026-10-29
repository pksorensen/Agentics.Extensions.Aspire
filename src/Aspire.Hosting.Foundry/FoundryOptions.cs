namespace Aspire.Hosting.Foundry;

public sealed class FoundryOptions
{
    public string Image { get; set; } = "ghcr.io/pksorensen/foundry-emulator";
    public string Tag { get; set; } = "latest";

    /// <summary>Build the image from a checkout of this repository instead of pulling
    /// it — while the emulator itself is what you are changing.</summary>
    public string? DockerfileContext { get; set; }
    public string DockerfilePath { get; set; } = "src/Foundry.Emulator/Dockerfile";

    /// <summary>Keep the live usage log (<c>usage.jsonl</c>) in a volume across runs.
    /// Seeded history is regenerated every start and never stored.</summary>
    public bool PersistData { get; set; }
    public string? DataVolumeName { get; set; }

    /// <summary>A fixed host port, published without the Aspire proxy. Pin it when a
    /// tool outside the AppHost — Codex in a terminal, a test runner — needs a base URL
    /// that does not move between runs.</summary>
    public int? HostPort { get; set; }

    /// <summary>The address clients use, if it is not <c>http://localhost:{HostPort}</c>.</summary>
    public string? PublicUrl { get; set; }

    /// <summary>False lets requests without any credential through as <c>anonymous</c>.
    /// The default is true, because a client that forgot its key should fail here the
    /// way it would against Azure.</summary>
    public bool RequireAuth { get; set; } = true;

    /// <summary>Accept the gateway simulator's <c>sim-…</c> keys (<c>sim-echo</c>,
    /// <c>sim-scenario:&lt;name&gt;</c>) as credentials that also pick a scenario.</summary>
    public bool AllowSimKeys { get; set; } = true;

    /// <summary>Characters per streamed delta.</summary>
    public int ChunkSize { get; set; } = 24;

    /// <summary>Pause between streamed deltas — a demo reads better when the text
    /// arrives at roughly model speed instead of all at once.</summary>
    public int ChunkDelayMs { get; set; }
}
