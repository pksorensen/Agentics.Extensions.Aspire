using System.Text;

namespace Aspire.Hosting.Testing.Videos;

/// <summary>
/// Generates and optionally runs an ffmpeg mux script that combines
/// a Playwright-recorded .webm video with timed TTS narration audio
/// and burned-in SRT subtitles.
/// </summary>
public static class VideoMuxer
{
    /// <summary>
    /// Write a shell script that muxes video + timed narration audio + subtitles.
    /// </summary>
    /// <param name="videosDir">Directory containing the video, audio, and subtitle files.</param>
    /// <param name="videoFile">Filename of the source .webm video (relative to videosDir).</param>
    /// <param name="entries">Narrator entries with start times and durations.</param>
    /// <param name="segments">TTS segments (may be null if no TTS was generated).</param>
    /// <param name="outputName">Base name for the output files (e.g. "devsetup" produces "devsetup-final.mp4").</param>
    /// <param name="srtFile">Filename of the SRT subtitle file (relative to videosDir).</param>
    /// <returns>Path to the generated mux script.</returns>
    public static string WriteMuxScript(
        string videosDir,
        string videoFile,
        IReadOnlyList<(TimeSpan Start, int DurationMs, string Text)> entries,
        List<NarrationSegment>? segments,
        string outputName = "demo",
        string? srtFile = null)
    {
        srtFile ??= $"{outputName}-narration.srt";

        var sb = new StringBuilder();
        sb.AppendLine("#!/bin/bash");
        sb.AppendLine("set -e");
        sb.AppendLine($"cd \"$(dirname \"$0\")\"");
        sb.AppendLine();
        sb.AppendLine($"VIDEO=\"{videoFile}\"");
        sb.AppendLine($"OUTPUT=\"{outputName}-final.mp4\"");
        sb.AppendLine($"OUTPUT_SUBS=\"{outputName}-with-subs.mp4\"");
        sb.AppendLine();

        var audioCount = 0;
        if (segments != null)
        {
            for (var i = 0; i < segments.Count; i++)
            {
                if (segments[i].AudioPath != null) audioCount++;
            }
        }

        if (audioCount == 0 || entries.Count == 0)
        {
            sb.AppendLine("# No TTS audio available — just re-encode video");
            sb.AppendLine("ffmpeg -y -i \"$VIDEO\" -c:v libx264 -preset fast -crf 23 \"$OUTPUT\"");
        }
        else
        {
            sb.AppendLine("# Mux video with timed narration audio");
            sb.Append("ffmpeg -y -i \"$VIDEO\"");
            for (var i = 0; i < audioCount; i++)
                sb.Append($" \\\n  -i narration-{i:D2}.mp3");

            sb.AppendLine(" \\");
            sb.Append("  -filter_complex \"");
            for (var i = 0; i < entries.Count && i < audioCount; i++)
            {
                var delayMs = (long)entries[i].Start.TotalMilliseconds;
                sb.Append($"[{i + 1}]adelay={delayMs}|{delayMs}[a{i}];");
            }
            for (var i = 0; i < audioCount; i++)
                sb.Append($"[a{i}]");
            sb.Append($"amix=inputs={audioCount}:duration=longest[aout]\"");

            sb.AppendLine(" \\");
            sb.AppendLine("  -map 0:v -map \"[aout]\" \\");
            sb.AppendLine("  -c:v libx264 -preset fast -crf 23 \\");
            sb.AppendLine("  -c:a aac -b:a 128k \\");
            sb.AppendLine("  -shortest \\");
            sb.AppendLine("  \"$OUTPUT\"");
        }

        sb.AppendLine();
        sb.AppendLine("echo \"Created: $OUTPUT\"");
        sb.AppendLine();

        sb.AppendLine("# Burn in subtitles");
        sb.AppendLine($"if [ -f {srtFile} ]; then");
        sb.AppendLine("  ffmpeg -y -i \"$OUTPUT\" \\");
        sb.AppendLine($"    -vf \"subtitles={srtFile}:force_style='FontSize=18,PrimaryColour=&H00FFFFFF,OutlineColour=&H00000000,Outline=2,MarginV=30'\" \\");
        sb.AppendLine("    -c:v libx264 -preset fast -crf 23 \\");
        sb.AppendLine("    -c:a copy \\");
        sb.AppendLine("    \"$OUTPUT_SUBS\"");
        sb.AppendLine("  echo \"Created: $OUTPUT_SUBS\"");
        sb.AppendLine("fi");

        var scriptPath = Path.Combine(videosDir, "mux.sh");
        File.WriteAllText(scriptPath, sb.ToString());
        try { System.Diagnostics.Process.Start("chmod", $"+x \"{scriptPath}\"")?.WaitForExit(); } catch { }

        return scriptPath;
    }

    /// <summary>
    /// Run the mux script and return the path to the final video with subtitles (or null on failure).
    /// </summary>
    public static async Task<string?> RunMuxScriptAsync(string scriptPath, string videosDir, string outputName = "demo")
    {
        var psi = new System.Diagnostics.ProcessStartInfo("bash", scriptPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        var proc = System.Diagnostics.Process.Start(psi);
        if (proc == null) return null;

        var stderr = await proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();

        if (proc.ExitCode != 0) return null;

        var withSubs = Path.Combine(videosDir, $"{outputName}-with-subs.mp4");
        if (File.Exists(withSubs)) return withSubs;

        var final = Path.Combine(videosDir, $"{outputName}-final.mp4");
        return File.Exists(final) ? final : null;
    }
}
