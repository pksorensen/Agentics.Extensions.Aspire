using System.Globalization;
using System.IO.Enumeration;
using System.Text.Json;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting;

internal sealed class TurbopackDiagnosticsAnnotation(
    JavaScript.JavaScriptAppResource resource) : IResourceAnnotation
{
    // The scan window is allocated once and reused for the life of the process,
    // and its initial size is deliberately below the 85,000-byte large-object
    // heap threshold.
    //
    // The previous implementation read the whole trace into a fresh array every
    // time the file changed. Next never truncates .next/dev/trace — it appends
    // across `next dev` restarts and keeps weeks of history — so that array grew
    // without bound, every instance landed on the LOH, and the LOH is swept but
    // not compacted. A long-lived AppHost paid for the entire file repeatedly to
    // learn about a few kilobytes of new spans.
    private const int InitialScanWindowBytes = 64 * 1024;

    // A single trace batch larger than this is skipped rather than buffered.
    // Next writes one JSON array per flush; batches are normally a few KB.
    private const int MaxScanWindowBytes = 4 * 1024 * 1024;

    private int _monitorStarted;
    private int _warningPublished;
    private byte[] _scanWindow = new byte[InitialScanWindowBytes];
    private long _traceOffset = -1;
    private long _monitorStartedUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private DateTimeOffset _lastCacheMeasurement = DateTimeOffset.MinValue;
    private long _lastCacheBytes = -1;

    public JavaScript.JavaScriptAppResource Resource { get; } = resource;
    public string WorkingDirectory => Resource.WorkingDirectory;
    public long CacheWarningLimitBytes { get; set; } = ByteSize.Parse("1gb");
    public long? AutomaticClearLimitBytes { get; set; }
    public TimeSpan SlowCompilationThreshold { get; set; } = TimeSpan.FromSeconds(10);
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);
    public TimeSpan CacheSizePollInterval { get; set; } = TimeSpan.FromMinutes(5);
    public bool ClearCommandAdded { get; set; }
    public bool AutomaticClearAdded { get; set; }
    public bool SlowStartDetectorAdded { get; set; }

    private string TracePath => Path.Combine(WorkingDirectory, ".next", "dev", "trace");

    public bool TryStartMonitor() => Interlocked.Exchange(ref _monitorStarted, 1) == 0;

    public bool TryMarkWarningPublished() =>
        Interlocked.Exchange(ref _warningPublished, 1) == 0;

    public void Reset()
    {
        Interlocked.Exchange(ref _warningPublished, 0);
        _monitorStartedUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _traceOffset = -1;
        _lastCacheMeasurement = DateTimeOffset.MinValue;
        _lastCacheBytes = -1;
    }

    public async Task<TurbopackWarning?> InspectAsync(CancellationToken cancellationToken)
    {
        if (InspectCache() is { } cacheWarning) return cacheWarning;

        long length;
        try
        {
            var info = new FileInfo(TracePath);
            if (!info.Exists) return null;
            length = info.Length;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }

        if (_traceOffset < 0)
        {
            // First look. The trace carries history from previous dev sessions
            // that the startTime filter would discard anyway, so start at the
            // end instead of parsing weeks of spans to find nothing.
            _traceOffset = length;
            return null;
        }

        // A cleared .next or a fresh trace file leaves the offset past the end.
        if (length < _traceOffset) _traceOffset = 0;
        if (length == _traceOffset) return null;

        try
        {
            return await ScanAppendedAsync(length, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private async Task<TurbopackWarning?> ScanAppendedAsync(
        long length,
        CancellationToken cancellationToken)
    {
        TurbopackWarning? slowest = null;

        var stream = new FileStream(
            TracePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 1,
            useAsync: true);

        await using (stream.ConfigureAwait(false))
        {
            while (_traceOffset < length)
            {
                stream.Position = _traceOffset;

                var window = (int)Math.Min(_scanWindow.Length, length - _traceOffset);
                var read = await FillWindowAsync(stream, window, cancellationToken).ConfigureAwait(false);
                if (read == 0) break;

                var consumed = ScanBatches(_scanWindow.AsSpan(0, read), ref slowest);
                if (consumed > 0)
                {
                    _traceOffset += consumed;
                    continue;
                }

                // No complete batch in this window. Either Next is mid-write and
                // the rest arrives before the next poll, or one batch is larger
                // than the window.
                if (read < _scanWindow.Length) break;
                if (TryGrowScanWindow()) break;

                // A batch exceeds the cap. Skip it rather than re-reading the
                // same window on every poll for the rest of the session.
                _traceOffset += read;
                break;
            }
        }

        return slowest;
    }

    private async Task<int> FillWindowAsync(
        FileStream stream,
        int count,
        CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < count)
        {
            var read = await stream
                .ReadAsync(_scanWindow.AsMemory(total, count - total), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0) break;
            total += read;
        }

        return total;
    }

    private bool TryGrowScanWindow()
    {
        if (_scanWindow.Length >= MaxScanWindowBytes) return false;
        _scanWindow = new byte[Math.Min(_scanWindow.Length * 2, MaxScanWindowBytes)];
        return true;
    }

    /// <summary>
    /// Scans whole trace batches out of <paramref name="bytes"/> and returns the
    /// number of bytes covered by complete batches. A partial trailing batch is
    /// left unconsumed so the next poll re-reads it.
    /// </summary>
    private long ScanBatches(ReadOnlySpan<byte> bytes, ref TurbopackWarning? slowest)
    {
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions
        {
            AllowMultipleValues = true,
        });

        long consumed = 0;
        try
        {
            while (reader.Read())
            {
                if (reader.TokenType is not JsonTokenType.StartArray) continue;

                using var batch = JsonDocument.ParseValue(ref reader);
                consumed = reader.BytesConsumed;
                InspectBatch(batch.RootElement, ref slowest);
            }

            consumed = reader.BytesConsumed;
        }
        catch (JsonException)
        {
            // Next appends complete JSON arrays. Landing between writes leaves a
            // partial trailing value; everything up to the last complete batch
            // is valid and the remainder is picked up on the next poll.
        }

        return consumed;
    }

    private void InspectBatch(JsonElement batch, ref TurbopackWarning? slowest)
    {
        foreach (var traceEvent in batch.EnumerateArray())
        {
            if (!traceEvent.TryGetProperty("name", out var nameElement)) continue;
            var name = nameElement.GetString();
            if (name is not ("compile-path" or "ensure-page")) continue;

            // Next records startTime as Unix milliseconds and duration as
            // microseconds.
            if (!traceEvent.TryGetProperty("startTime", out var startElement) ||
                startElement.GetDouble() < _monitorStartedUnixMilliseconds)
            {
                continue;
            }

            if (!traceEvent.TryGetProperty("duration", out var durationElement)) continue;
            var duration = TimeSpan.FromMilliseconds(durationElement.GetDouble() / 1_000d);
            if (duration < SlowCompilationThreshold) continue;

            if (slowest is null || duration > slowest.Duration)
            {
                slowest = new TurbopackWarning(
                    $"Slow Next.js compile: {duration.TotalSeconds:F1}s",
                    $"{TraceTarget(traceEvent, name)} exceeded the {SlowCompilationThreshold.TotalSeconds:F0}s threshold.",
                    LastMeasuredCacheBytes(),
                    duration);
            }
        }
    }

    private TurbopackWarning? InspectCache()
    {
        var now = DateTimeOffset.UtcNow;
        if (now - _lastCacheMeasurement < CacheSizePollInterval) return null;

        _lastCacheMeasurement = now;
        _lastCacheBytes = TurbopackCache.GetSize(WorkingDirectory);
        if (_lastCacheBytes < CacheWarningLimitBytes) return null;

        return new TurbopackWarning(
            $"Turbopack cache is {ByteSize.Format(_lastCacheBytes)}",
            $"The configured warning limit is {ByteSize.Format(CacheWarningLimitBytes)}.",
            _lastCacheBytes);
    }

    /// <summary>
    /// The most recent cache measurement, taken on the cache poll interval. A
    /// slow-compile warning reports it rather than walking the cache tree again
    /// for every candidate span.
    /// </summary>
    private long LastMeasuredCacheBytes()
    {
        if (_lastCacheBytes >= 0) return _lastCacheBytes;

        _lastCacheMeasurement = DateTimeOffset.UtcNow;
        _lastCacheBytes = TurbopackCache.GetSize(WorkingDirectory);
        return _lastCacheBytes;
    }

    private static string TraceTarget(JsonElement traceEvent, string fallback)
    {
        if (!traceEvent.TryGetProperty("tags", out var tags)) return fallback;
        foreach (var property in new[] { "trigger", "inputPage", "url" })
        {
            if (tags.TryGetProperty(property, out var value) && value.ValueKind is JsonValueKind.String)
            {
                return value.GetString() ?? fallback;
            }
        }

        return fallback;
    }
}

internal sealed record TurbopackWarning(
    string Summary,
    string Detail,
    long CacheBytes,
    TimeSpan? Duration = null);

internal static class TurbopackCache
{
    public static long GetSize(string workingDirectory)
    {
        long total = 0;
        foreach (var directory in Paths(workingDirectory))
        {
            if (!Directory.Exists(directory)) continue;

            try
            {
                // FileSystemEnumerable reports the size from the directory entry
                // the OS already read, so this walk allocates neither a path
                // string nor a FileInfo per file.
                var files = new FileSystemEnumerable<long>(
                    directory,
                    static (ref FileSystemEntry entry) => entry.Length,
                    new EnumerationOptions
                    {
                        RecurseSubdirectories = true,
                        IgnoreInaccessible = true,
                        AttributesToSkip = FileAttributes.ReparsePoint,
                    })
                {
                    ShouldIncludePredicate = static (ref FileSystemEntry entry) => !entry.IsDirectory,
                };

                foreach (var length in files)
                {
                    try
                    {
                        total = checked(total + length);
                    }
                    catch (OverflowException) { return long.MaxValue; }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        return total;
    }

    public static long Clear(string workingDirectory, ILogger logger)
    {
        var size = GetSize(workingDirectory);
        foreach (var directory in Paths(workingDirectory))
        {
            if (!Directory.Exists(directory)) continue;
            Directory.Delete(directory, recursive: true);
            logger.LogInformation("Cleared Turbopack cache at {CacheDirectory}", directory);
        }

        return size;
    }

    private static IEnumerable<string> Paths(string workingDirectory)
    {
        // Next 16 writes development state below .next/dev. Older versions use
        // .next/cache directly; supporting both keeps the extension harmless
        // across a framework upgrade or downgrade.
        yield return Path.Combine(workingDirectory, ".next", "dev", "cache", "turbopack");
        yield return Path.Combine(workingDirectory, ".next", "cache", "turbopack");
    }
}

internal static class ByteSize
{
    public static long Parse(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        var normalized = value.Trim().ToLowerInvariant().Replace(" ", string.Empty);
        var suffixLength = normalized.TakeLastWhile(char.IsLetter).Count();
        var numberPart = suffixLength == 0 ? normalized : normalized[..^suffixLength];
        var suffix = suffixLength == 0 ? "b" : normalized[^suffixLength..];

        if (!double.TryParse(numberPart, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ||
            number < 0 || double.IsNaN(number) || double.IsInfinity(number))
        {
            throw new ArgumentException($"Invalid byte size '{value}'.", nameof(value));
        }

        var multiplier = suffix switch
        {
            "b" => 1d,
            "kb" or "kib" => 1_024d,
            "mb" or "mib" => 1_024d * 1_024d,
            "gb" or "gib" => 1_024d * 1_024d * 1_024d,
            "tb" or "tib" => 1_024d * 1_024d * 1_024d * 1_024d,
            _ => throw new ArgumentException($"Unsupported byte-size suffix in '{value}'.", nameof(value)),
        };

        var bytes = number * multiplier;
        if (bytes > long.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(value), value, "Byte size is too large.");

        return checked((long)bytes);
    }

    public static string Format(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        var value = (double)bytes;
        var unit = 0;
        while (value >= 1_024 && unit < units.Length - 1)
        {
            value /= 1_024;
            unit++;
        }

        return $"{value:F1} {units[unit]}";
    }
}

internal static class EnumerableExtensions
{
    public static IEnumerable<T> TakeLastWhile<T>(
        this IEnumerable<T> source,
        Func<T, bool> predicate)
    {
        return source.Reverse().TakeWhile(predicate).Reverse();
    }
}
