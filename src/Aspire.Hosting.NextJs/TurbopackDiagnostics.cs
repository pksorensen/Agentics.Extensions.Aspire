using System.Globalization;
using System.Text.Json;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting;

internal sealed class TurbopackDiagnosticsAnnotation(
    JavaScript.JavaScriptAppResource resource) : IResourceAnnotation
{
    private int _monitorStarted;
    private int _warningPublished;
    private long _traceLength = -1;
    private long _monitorStartedUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private DateTimeOffset _lastCacheMeasurement = DateTimeOffset.MinValue;

    public JavaScript.JavaScriptAppResource Resource { get; } = resource;
    public string WorkingDirectory => Resource.WorkingDirectory;
    public long CacheWarningLimitBytes { get; set; } = ByteSize.Parse("1gb");
    public long? AutomaticClearLimitBytes { get; set; }
    public TimeSpan SlowCompilationThreshold { get; set; } = TimeSpan.FromSeconds(10);
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);
    public TimeSpan CacheSizePollInterval { get; set; } = TimeSpan.FromSeconds(30);
    public bool ClearCommandAdded { get; set; }
    public bool AutomaticClearAdded { get; set; }
    public bool SlowStartDetectorAdded { get; set; }

    public bool TryStartMonitor() => Interlocked.Exchange(ref _monitorStarted, 1) == 0;

    public bool TryMarkWarningPublished() =>
        Interlocked.Exchange(ref _warningPublished, 1) == 0;

    public void Reset()
    {
        Interlocked.Exchange(ref _warningPublished, 0);
        _monitorStartedUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _traceLength = GetTraceLength();
        _lastCacheMeasurement = DateTimeOffset.MinValue;
    }

    public async Task<TurbopackWarning?> InspectAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        if (now - _lastCacheMeasurement >= CacheSizePollInterval)
        {
            _lastCacheMeasurement = now;
            var cacheBytes = TurbopackCache.GetSize(WorkingDirectory);
            if (cacheBytes >= CacheWarningLimitBytes)
            {
                return new TurbopackWarning(
                    $"Turbopack cache is {ByteSize.Format(cacheBytes)}",
                    $"The configured warning limit is {ByteSize.Format(CacheWarningLimitBytes)}.",
                    cacheBytes);
            }
        }

        var tracePath = Path.Combine(WorkingDirectory, ".next", "dev", "trace");
        if (!File.Exists(tracePath)) return null;

        var traceLength = new FileInfo(tracePath).Length;
        if (traceLength == _traceLength) return null;

        try
        {
            var bytes = await File.ReadAllBytesAsync(tracePath, cancellationToken).ConfigureAwait(false);
            var warning = FindSlowCompilation(bytes);
            _traceLength = traceLength;
            return warning;
        }
        catch (JsonException)
        {
            // Next appends complete JSON arrays to this file. If inspection lands
            // between writes, leave the length untouched and retry next poll.
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private TurbopackWarning? FindSlowCompilation(ReadOnlySpan<byte> bytes)
    {
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions
        {
            AllowMultipleValues = true,
        });

        TurbopackWarning? slowest = null;
        while (reader.Read())
        {
            if (reader.TokenType is not JsonTokenType.StartArray) continue;

            using var batch = JsonDocument.ParseValue(ref reader);
            foreach (var traceEvent in batch.RootElement.EnumerateArray())
            {
                if (!traceEvent.TryGetProperty("name", out var nameElement)) continue;
                var name = nameElement.GetString();
                if (name is not ("compile-path" or "ensure-page")) continue;

                if (!traceEvent.TryGetProperty("startTime", out var startElement) ||
                    startElement.GetDouble() < _monitorStartedUnixMilliseconds)
                {
                    continue;
                }

                if (!traceEvent.TryGetProperty("duration", out var durationElement)) continue;
                var duration = TimeSpan.FromMilliseconds(durationElement.GetDouble() / 1_000d);
                if (duration < SlowCompilationThreshold) continue;

                var target = TraceTarget(traceEvent, name);
                if (slowest is null || duration > slowest.Duration)
                {
                    slowest = new TurbopackWarning(
                        $"Slow Next.js compile: {duration.TotalSeconds:F1}s",
                        $"{target} exceeded the {SlowCompilationThreshold.TotalSeconds:F0}s threshold.",
                        TurbopackCache.GetSize(WorkingDirectory),
                        duration);
                }
            }
        }

        return slowest;
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

    private long GetTraceLength()
    {
        var path = Path.Combine(WorkingDirectory, ".next", "dev", "trace");
        return File.Exists(path) ? new FileInfo(path).Length : -1;
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
                foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        total = checked(total + new FileInfo(file).Length);
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
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
