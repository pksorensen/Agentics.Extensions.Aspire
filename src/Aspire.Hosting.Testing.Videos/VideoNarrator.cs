using System.Text;

namespace Aspire.Hosting.Testing.Videos;

/// <summary>
/// Records narrator events with timestamps relative to recording start.
/// Supports two modes:
///   1. Free-running: timestamps captured at call time (default)
///   2. TTS-driven: durations from pre-generated audio drive subtitle timing
/// Exports to SRT, WebVTT, and plain text script formats.
/// </summary>
public class VideoNarrator
{
    private DateTime _startTime;
    private readonly List<SubtitleEntry> _entries = new();
    private readonly Dictionary<int, int> _durations = new(); // index → durationMs from TTS

    public VideoNarrator()
    {
        _startTime = DateTime.UtcNow;
    }

    /// <summary>
    /// Reset the clock to now. Call this right after the video recording starts
    /// (after NewPageAsync) so narrator timestamps align with the video timeline.
    /// </summary>
    public void ResetClock()
    {
        _startTime = DateTime.UtcNow;
    }

    /// <summary>All narration lines in order (for pre-generating TTS before recording).</summary>
    public IReadOnlyList<string> Lines => _lines;
    private readonly List<string> _lines = new();

    /// <summary>
    /// Register a narration line for later TTS generation (call before recording starts).
    /// Returns the line index.
    /// </summary>
    public int AddLine(string text)
    {
        _lines.Add(text);
        return _lines.Count - 1;
    }

    /// <summary>
    /// Load TTS durations from pre-generated segments.
    /// Must be called after TTS generation and before recording starts.
    /// </summary>
    public void LoadDurations(List<NarrationSegment> segments)
    {
        for (var i = 0; i < segments.Count; i++)
        {
            _durations[i] = segments[i].DurationMs;
        }
    }

    /// <summary>
    /// Record that a narrator line is being spoken now.
    /// Returns the TTS duration in ms (so the caller can wait for the audio to finish).
    /// Returns 0 if no TTS duration is available.
    /// </summary>
    public int Say(string text)
    {
        var elapsed = DateTime.UtcNow - _startTime;
        var index = _entries.Count;
        _entries.Add(new SubtitleEntry
        {
            Index = index + 1,
            Start = elapsed,
            Text = text,
        });

        return _durations.TryGetValue(index, out var durationMs) ? durationMs : 0;
    }

    /// <summary>
    /// Get entries as tuples for external use (e.g. mux script generation).
    /// </summary>
    public IReadOnlyList<(TimeSpan Start, int DurationMs, string Text)> GetEntries()
    {
        return Finalized().Select(e => (e.Start, (int)(e.End - e.Start).TotalMilliseconds, e.Text)).ToList();
    }

    private List<SubtitleEntry> Finalized()
    {
        var result = new List<SubtitleEntry>();
        for (var i = 0; i < _entries.Count; i++)
        {
            var entry = _entries[i];
            if (_durations.TryGetValue(i, out var durationMs))
            {
                entry.End = entry.Start + TimeSpan.FromMilliseconds(durationMs);
            }
            else
            {
                entry.End = i + 1 < _entries.Count
                    ? _entries[i + 1].Start
                    : entry.Start + TimeSpan.FromSeconds(4);
            }
            result.Add(entry);
        }
        return result;
    }

    /// <summary>Write SRT subtitle file.</summary>
    public void WriteSrt(string path)
    {
        var sb = new StringBuilder();
        foreach (var e in Finalized())
        {
            sb.AppendLine(e.Index.ToString());
            sb.AppendLine($"{FormatSrt(e.Start)} --> {FormatSrt(e.End)}");
            sb.AppendLine(e.Text);
            sb.AppendLine();
        }
        File.WriteAllText(path, sb.ToString());
    }

    /// <summary>Write WebVTT subtitle file.</summary>
    public void WriteVtt(string path)
    {
        var sb = new StringBuilder();
        sb.AppendLine("WEBVTT");
        sb.AppendLine();
        foreach (var e in Finalized())
        {
            sb.AppendLine($"{FormatVtt(e.Start)} --> {FormatVtt(e.End)}");
            sb.AppendLine(e.Text);
            sb.AppendLine();
        }
        File.WriteAllText(path, sb.ToString());
    }

    /// <summary>Write plain text script with timestamps (for voiceover recording).</summary>
    public void WriteScript(string path)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Narration Script");
        sb.AppendLine($"# Generated: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
        sb.AppendLine($"# Total duration: {FormatVtt(DateTime.UtcNow - _startTime)}");
        sb.AppendLine();
        foreach (var e in Finalized())
        {
            var dur = _durations.TryGetValue(e.Index - 1, out var ms) ? $" ({ms}ms)" : "";
            sb.AppendLine($"[{FormatVtt(e.Start)}]{dur} {e.Text}");
        }
        File.WriteAllText(path, sb.ToString());
    }

    private static string FormatSrt(TimeSpan ts)
        => $"{(int)ts.TotalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2},{ts.Milliseconds:D3}";

    private static string FormatVtt(TimeSpan ts)
        => $"{(int)ts.TotalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}.{ts.Milliseconds:D3}";

    private class SubtitleEntry
    {
        public int Index;
        public TimeSpan Start;
        public TimeSpan End;
        public string Text = "";
    }
}
