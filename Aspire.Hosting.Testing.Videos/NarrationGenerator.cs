using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Aspire.Hosting.Testing.Videos;

/// <summary>
/// Generates TTS audio for narration lines using an Azure OpenAI (or compatible) TTS
/// endpoint. Returns audio file paths and measured durations so the video test can pace
/// itself to match the voiceover.
///
/// Caches generated audio in ~/.pks-narrations/ keyed by content hash.
///
/// The endpoint and credentials are supplied entirely by configuration — nothing is
/// baked in. Resolution order:
/// <list type="number">
///   <item>An Agentics runner proxy — <c>AGENTICS_PROXY_URL</c> + <c>AGENTICS_PROXY_TOKEN</c>
///     (two-token capability flow; the upstream host it authorizes comes from
///     <c>AZURE_TTS_HOST</c> or the host of <c>AZURE_TTS_ENDPOINT</c>).</item>
///   <item>A single-token proxy — <c>FOUNDRY_PROXY_URL</c> + <c>FOUNDRY_PROXY_TOKEN</c>.</item>
///   <item>A direct endpoint + key — the <c>endpoint</c>/<c>apiKey</c> constructor args, or
///     <c>AZURE_TTS_ENDPOINT</c> + <c>AZURE_TTS_API_KEY</c>.</item>
/// </list>
/// When none of these yield a usable endpoint, generation is skipped silently and each line
/// falls back to an estimated duration (<see cref="IsAvailable"/> is <c>false</c>).
/// </summary>
public class NarrationGenerator
{
    // Azure OpenAI speech path appended to a resource/proxy base URL. The deployment name
    // and api-version are overridable via AZURE_TTS_DEPLOYMENT / AZURE_TTS_API_VERSION.
    private static string SpeechPath() =>
        $"/openai/deployments/{TtsDeployment}/audio/speech?api-version={TtsApiVersion}";

    private static string TtsDeployment =>
        Environment.GetEnvironmentVariable("AZURE_TTS_DEPLOYMENT") is { Length: > 0 } d ? d : "tts-hd";
    private static string TtsApiVersion =>
        Environment.GetEnvironmentVariable("AZURE_TTS_API_VERSION") is { Length: > 0 } v ? v : "2025-03-01-preview";

    private const string DefaultVoice = "alloy";
    private static string DefaultModel => TtsDeployment;

    private static readonly string CacheDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".pks-narrations");

    private readonly string? _apiKey;
    private readonly string? _endpoint;
    private readonly string _voice;
    private readonly HttpClient _http;

    // Upstream host the runner proxy is asked to mint a capability token for.
    private readonly string? _ttsHost;

    public NarrationGenerator(string? apiKey, string? endpoint = null, string? voice = null)
    {
        _voice = voice ?? DefaultVoice;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        // Direct endpoint may come from the arg or AZURE_TTS_ENDPOINT; used by the direct path
        // and to derive the upstream host for the runner-proxy capability request.
        var directEndpoint = endpoint
            ?? Environment.GetEnvironmentVariable("AZURE_TTS_ENDPOINT");
        _ttsHost = Environment.GetEnvironmentVariable("AZURE_TTS_HOST")
            ?? (Uri.TryCreate(directEndpoint, UriKind.Absolute, out var eu) ? eu.Host : null);

        // Priority 1: AgenticsProxy (runner-managed, two-token flow).
        // AGENTICS_PROXY_URL + AGENTICS_PROXY_TOKEN are injected by pks agentics runner.
        var agenticsProxyUrl   = Environment.GetEnvironmentVariable("AGENTICS_PROXY_URL");
        var agenticsProxyToken = Environment.GetEnvironmentVariable("AGENTICS_PROXY_TOKEN");
        if (!string.IsNullOrEmpty(agenticsProxyUrl) && !string.IsNullOrEmpty(agenticsProxyToken))
        {
            _agenticsProxyUrl   = agenticsProxyUrl.TrimEnd('/');
            _agenticsProxyToken = agenticsProxyToken;
            // _apiKey / bearer resolved lazily per-request via AcquireCapabilityTokenAsync
            _apiKey   = null;
            _endpoint = _agenticsProxyUrl + SpeechPath();
            return;
        }

        // Priority 2: simple FoundryProxy (single-token).
        var proxyUrl   = Environment.GetEnvironmentVariable("FOUNDRY_PROXY_URL");
        var proxyToken = Environment.GetEnvironmentVariable("FOUNDRY_PROXY_TOKEN");
        if (!string.IsNullOrEmpty(proxyUrl) && !string.IsNullOrEmpty(proxyToken))
        {
            _apiKey   = proxyToken;
            _endpoint = proxyUrl.TrimEnd('/') + SpeechPath();
            return;
        }

        // Priority 3: direct endpoint + key (constructor args or AZURE_TTS_* env).
        _apiKey   = apiKey ?? Environment.GetEnvironmentVariable("AZURE_TTS_API_KEY");
        _endpoint = directEndpoint;
    }

    private readonly string? _agenticsProxyUrl;
    private readonly string? _agenticsProxyToken;
    private string? _capabilityToken;

    // Acquires (or reuses) a capability token from the AgenticsProxy token endpoint.
    private async Task<string?> AcquireCapabilityTokenAsync()
    {
        if (_capabilityToken != null)
            return _capabilityToken;

        if (string.IsNullOrEmpty(_ttsHost))
            return null;

        try
        {
            using var req = new System.Net.Http.HttpRequestMessage(
                System.Net.Http.HttpMethod.Post, $"{_agenticsProxyUrl}/api/token");
            req.Content = new System.Net.Http.StringContent(
                $"{{\"host\":\"{_ttsHost}\"}}",
                System.Text.Encoding.UTF8, "application/json");
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer", _agenticsProxyToken);

            var resp = await _http.SendAsync(req);
            resp.EnsureSuccessStatusCode();
            using var doc = System.Text.Json.JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            _capabilityToken = doc.RootElement.GetProperty("token").GetString();
        }
        catch { }

        return _capabilityToken;
    }

    public bool IsAvailable =>
        !string.IsNullOrEmpty(_endpoint) && (!string.IsNullOrEmpty(_apiKey) || _agenticsProxyUrl != null);

    private string CacheKey(string text)
    {
        var input = $"{_voice}:{text}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private NarrationSegment? TryLoadFromCache(string text, string outputPath)
    {
        var key = CacheKey(text);
        var cachedMp3 = Path.Combine(CacheDir, $"{key}.mp3");
        var cachedMeta = Path.Combine(CacheDir, $"{key}.json");

        if (!File.Exists(cachedMp3) || !File.Exists(cachedMeta))
            return null;

        try
        {
            File.Copy(cachedMp3, outputPath, overwrite: true);
            var meta = JsonSerializer.Deserialize<CachedMeta>(File.ReadAllText(cachedMeta));
            return new NarrationSegment
            {
                Text = text,
                AudioPath = outputPath,
                DurationMs = meta?.DurationMs ?? 3000,
            };
        }
        catch
        {
            return null;
        }
    }

    private void SaveToCache(string text, string audioPath, int durationMs)
    {
        try
        {
            Directory.CreateDirectory(CacheDir);
            var key = CacheKey(text);
            File.Copy(audioPath, Path.Combine(CacheDir, $"{key}.mp3"), overwrite: true);
            var meta = JsonSerializer.Serialize(new CachedMeta { DurationMs = durationMs, Text = text });
            File.WriteAllText(Path.Combine(CacheDir, $"{key}.json"), meta);
        }
        catch { }
    }

    /// <summary>
    /// Generate TTS audio for a list of narration lines.
    /// </summary>
    public async Task<List<NarrationSegment>> GenerateAsync(
        IReadOnlyList<string> lines, string outputDir)
    {
        Directory.CreateDirectory(outputDir);
        var segments = new List<NarrationSegment>();

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var audioPath = Path.Combine(outputDir, $"narration-{i:D2}.mp3");

            if (!IsAvailable)
            {
                var wordCount = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
                segments.Add(new NarrationSegment
                {
                    Text = line,
                    AudioPath = null,
                    DurationMs = Math.Max(2000, wordCount * 150),
                });
                continue;
            }

            var cached = TryLoadFromCache(line, audioPath);
            if (cached != null)
            {
                segments.Add(cached);
                continue;
            }

            try
            {
                var body = JsonSerializer.Serialize(new
                {
                    model = DefaultModel,
                    input = line,
                    voice = _voice,
                });

                // Resolve bearer token: AgenticsProxy uses a capability token, others use _apiKey directly.
                var bearerToken = _agenticsProxyUrl != null
                    ? await AcquireCapabilityTokenAsync()
                    : _apiKey;

                var request = new HttpRequestMessage(HttpMethod.Post, _endpoint);
                request.Content = new StringContent(body, Encoding.UTF8, "application/json");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);

                var response = await _http.SendAsync(request);
                response.EnsureSuccessStatusCode();

                var audioBytes = await response.Content.ReadAsByteArrayAsync();
                await File.WriteAllBytesAsync(audioPath, audioBytes);

                var durationMs = await GetAudioDurationMsAsync(audioPath);
                segments.Add(new NarrationSegment
                {
                    Text = line,
                    AudioPath = audioPath,
                    DurationMs = durationMs,
                });

                SaveToCache(line, audioPath, durationMs);
            }
            catch (Exception ex)
            {
                var wordCount = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
                segments.Add(new NarrationSegment
                {
                    Text = line,
                    AudioPath = null,
                    DurationMs = Math.Max(2000, wordCount * 150),
                    Error = ex.Message,
                });
            }
        }

        return segments;
    }

    private static async Task<int> GetAudioDurationMsAsync(string audioPath)
    {
        try
        {
            var psi = new ProcessStartInfo("ffprobe",
                $"-v error -show_entries format=duration -of csv=p=0 \"{audioPath}\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            var proc = Process.Start(psi);
            if (proc == null) return 3000;

            var output = await proc.StandardOutput.ReadToEndAsync();
            await proc.WaitForExitAsync();

            if (double.TryParse(output.Trim(), System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var seconds))
            {
                return (int)(seconds * 1000);
            }
        }
        catch { }

        var fileSize = new FileInfo(audioPath).Length;
        return (int)(fileSize * 8.0 / 128.0);
    }

    /// <summary>
    /// Concatenate all generated audio files into a single file using ffmpeg.
    /// </summary>
    public static async Task<string?> ConcatenateAsync(
        List<NarrationSegment> segments, string outputDir)
    {
        var audioFiles = segments
            .Where(s => s.AudioPath != null)
            .Select(s => s.AudioPath!)
            .ToList();

        if (audioFiles.Count == 0) return null;

        var listFile = Path.Combine(outputDir, "concat-list.txt");
        var outputFile = Path.Combine(outputDir, "narration-full.mp3");

        var listContent = string.Join("\n",
            audioFiles.Select(f => $"file '{f}'"));
        await File.WriteAllTextAsync(listFile, listContent);

        try
        {
            var psi = new ProcessStartInfo("ffmpeg",
                $"-y -f concat -safe 0 -i \"{listFile}\" -c copy \"{outputFile}\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            var proc = Process.Start(psi);
            if (proc == null) return null;

            await proc.WaitForExitAsync();
            return proc.ExitCode == 0 ? outputFile : null;
        }
        catch
        {
            return null;
        }
    }

    private class CachedMeta
    {
        public int DurationMs { get; set; }
        public string Text { get; set; } = "";
    }
}

public class NarrationSegment
{
    public string Text { get; set; } = "";
    public string? AudioPath { get; set; }
    public int DurationMs { get; set; }
    public string? Error { get; set; }
}
