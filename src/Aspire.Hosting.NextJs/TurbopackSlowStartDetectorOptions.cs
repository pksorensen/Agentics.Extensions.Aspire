namespace Aspire.Hosting;

/// <summary>
/// Configures Next.js development-start diagnostics for a JavaScript resource.
/// </summary>
public sealed class TurbopackSlowStartDetectorOptions
{
    /// <summary>
    /// A completed Next.js compile span at or above this duration triggers a warning.
    /// </summary>
    public TimeSpan SlowCompilationThreshold { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How often the detector checks for new Next.js trace events.
    /// </summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How often the detector measures the on-disk Turbopack cache.
    /// </summary>
    public TimeSpan CacheSizePollInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Cache size at which the detector recommends clearing the cache.
    /// Supports binary units such as <c>512mb</c>, <c>1gb</c>, and <c>1.5 GB</c>.
    /// </summary>
    public string CacheWarningLimit { get; set; } = "1gb";
}
