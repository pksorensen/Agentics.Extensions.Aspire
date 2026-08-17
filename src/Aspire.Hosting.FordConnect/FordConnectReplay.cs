using System.Text.Json;
using System.Text.Json.Nodes;

namespace Aspire.Hosting.FordConnect.Emulator;

/// <summary>
/// A recorded car, played back.
///
/// The simulated car in <see cref="FordConnectCar"/> reproduces Ford's *behaviour* from
/// measurements. This reproduces a specific afternoon: the exact payloads Ford sent,
/// with the gaps it sent them in. The two answer different questions, which is why both
/// exist — the simulator is the daily driver and answers "does my client cope with a car
/// that reports when it feels like it", replay answers "does my client cope with what
/// actually arrived on 17 August", including whatever was in it that nobody thought to
/// simulate.
///
/// The input is the platform's own event log: JSONL, one object per line, and the
/// <c>ford.reading</c> rows are the ones that matter. Those rows exist because the
/// Query API has no history endpoint — an hour not logged is an hour gone — and having
/// written them down, replaying them is nearly free.
///
/// Three rules, each of which is a decision:
///
///   - <b>The recorded payload is served byte for byte.</b> No re-stamping, not even of
///     the envelope. A replay that rewrites timestamps to look fresh is a simulator
///     with extra steps, and the point of this mode is that the bytes are not ours.
///   - <b>Time is scaled, not resampled.</b> With <c>ReplaySpeed = 60</c> an hour of log
///     becomes a minute, and a 42-minute silence becomes 42 seconds — still a silence.
///     Polling inside one returns the same reading again, which is precisely the
///     behaviour a cadence experiment needs to meet.
///   - <b>The end holds; it does not loop.</b> Looping would run the odometer backwards
///     and no car does that, so a client that trusted a monotonic odometer would break
///     on a fiction rather than on anything Ford does.
/// </summary>
public sealed class FordConnectReplay
{
    private readonly object _gate = new();
    private readonly Dictionary<string, List<Row>> _byVin = new(StringComparer.OrdinalIgnoreCase);
    private readonly double _speed;

    /// <summary>Log time of the first reading in the file, across every VIN. One anchor
    /// for the whole file rather than one per car: two cars recorded together must play
    /// back together, or a client correlating them sees a world that never existed.</summary>
    private readonly DateTimeOffset _logStart;

    private DateTimeOffset? _wallStart;

    private sealed record Row(DateTimeOffset At, JsonNode Telemetry);

    public FordConnectReplay(string path, double speed)
    {
        _speed = speed > 0 ? speed : 1.0;

        if (!File.Exists(path))
            throw new FileNotFoundException(
                $"Replay mode was asked for but '{path}' does not exist inside the container. The path is a "
                + "CONTAINER path — mount the export with WithReplayLog(hostPath), which is what sets both.",
                path);

        var skipped = 0;
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            JsonNode? row;
            try { row = JsonNode.Parse(line); }
            catch (JsonException) { skipped++; continue; }

            if (row?["kind"]?.GetValue<string>() != "ford.reading") continue;

            var telemetry = row["telemetry"];
            var vin = row["vin"]?.GetValue<string>();
            if (telemetry is null || string.IsNullOrWhiteSpace(vin)) { skipped++; continue; }

            // `at` is when we wrote the row. Deliberately not `measuredAt`: that is the
            // envelope's stamp, which is Ford's record clock and was measured sitting
            // ahead of every signal inside it — ordering by it would reorder the log
            // against the order the readings actually arrived in.
            if (!DateTimeOffset.TryParse(row["at"]?.GetValue<string>(), out var at)) { skipped++; continue; }

            if (!_byVin.TryGetValue(vin, out var rows)) _byVin[vin] = rows = [];
            rows.Add(new Row(at, telemetry.DeepClone()));
        }

        foreach (var rows in _byVin.Values) rows.Sort((a, b) => a.At.CompareTo(b.At));

        if (_byVin.Count == 0)
            throw new InvalidOperationException(
                $"'{path}' holds no `ford.reading` rows with a vin and a telemetry payload ({skipped} lines "
                + "could not be used). An empty replay would look exactly like a car that never reports, which "
                + "is a bug this emulator would then hide rather than expose.");

        _logStart = _byVin.Values.Min(rows => rows[0].At);
        Skipped = skipped;
    }

    public int Skipped { get; }
    public IReadOnlyCollection<string> Vins => _byVin.Keys;
    public int RowCount => _byVin.Values.Sum(rows => rows.Count);

    /// <summary>Where playback has reached, in log time. Null until the first read: the
    /// clock starts when somebody asks, so a slow AppHost start does not silently
    /// consume the first ten minutes of the recording.</summary>
    public DateTimeOffset? Position
    {
        get { lock (_gate) return _wallStart is null ? null : LogNow(DateTimeOffset.UtcNow); }
    }

    public DateTimeOffset LogStart => _logStart;
    public DateTimeOffset LogEnd => _byVin.Values.Max(rows => rows[^1].At);
    public double Speed => _speed;

    /// <summary>How many readings of this car playback has passed, and how many there
    /// are. What the portal shows instead of an ignition button, because there is
    /// nothing to drive here — the driving already happened.</summary>
    public (int Played, int Total) Progress(string vin)
    {
        if (!_byVin.TryGetValue(vin, out var rows)) return (0, 0);
        lock (_gate)
        {
            if (_wallStart is null) return (0, rows.Count);
            var at = LogNow(DateTimeOffset.UtcNow);
            return (rows.Count(r => r.At <= at), rows.Count);
        }
    }

    /// <summary>
    /// The reading this car had reported by now, as recorded.
    ///
    /// Returns the last row at or before the current log position, so a poll inside a
    /// recorded silence returns the same payload again — the client cannot tell that
    /// from the real thing, which is the entire point. Before the first row (possible
    /// when one car starts later than another) the first row is served rather than
    /// nothing: a 404 would mean "no such car", and there is such a car.
    /// </summary>
    public JsonNode? Telemetry(string vin)
    {
        if (!_byVin.TryGetValue(vin, out var rows)) return null;

        lock (_gate)
        {
            _wallStart ??= DateTimeOffset.UtcNow;
            var at = LogNow(DateTimeOffset.UtcNow);

            var row = rows[0];
            foreach (var candidate in rows)
            {
                if (candidate.At > at) break;
                row = candidate;
            }

            // A copy per read. The caller serialises it, and handing out the same node
            // twice would let one reader's serializer settings surprise the next.
            return row.Telemetry.DeepClone();
        }
    }

    private DateTimeOffset LogNow(DateTimeOffset wall) =>
        _logStart + TimeSpan.FromMilliseconds((wall - _wallStart!.Value).TotalMilliseconds * _speed);
}
