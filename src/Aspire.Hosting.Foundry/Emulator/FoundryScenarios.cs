using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using YamlDotNet.RepresentationModel;

namespace Aspire.Hosting.Foundry.Emulator;

/// <summary>
/// A scripted conversation. Playback is <b>stateless</b>: the step to serve is
/// derived from the request's own history (how many assistant turns it already
/// carries), not from a server-side cursor. A coding agent re-sends the whole
/// conversation on every call, so a retry, a reconnect or a second agent replaying
/// the same transcript all get the same answer — which a cursor cannot promise.
/// </summary>
public sealed class FoundryScenario
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public FoundryScenarioTrigger? Trigger { get; set; }

    /// <summary><c>fallback</c> (default): answer with the fallback text once the
    /// script is used up. <c>last</c>: repeat the last step. <c>error</c>: 400.</summary>
    public string OnExhausted { get; set; } = "fallback";

    public List<FoundryScenarioStep> Steps { get; set; } = [];
}

public sealed class FoundryScenarioTrigger
{
    /// <summary>Matched against the conversation's first real user message. A
    /// scenario with a trigger is chosen automatically when the request names no
    /// scenario — "type <c>byg …</c> into Codex and the script starts".</summary>
    public string? PromptRegex { get; set; }

    /// <summary>Optional: only for these deployment names.</summary>
    public List<string>? Deployments { get; set; }
}

public sealed class FoundryScenarioStep
{
    /// <summary>Assistant text for this turn. Substitutions: <c>$lastUserText</c>,
    /// <c>$firstUserText</c>, <c>$prompt</c> (the trigger's capture group 1, else the
    /// first user text), <c>$model</c>, <c>$step</c>.</summary>
    public string? Text { get; set; }

    /// <summary>A reasoning summary, streamed as a <c>reasoning</c> item.</summary>
    public string? Reasoning { get; set; }

    public List<FoundryScenarioCall> Calls { get; set; } = [];
    public FoundryScenarioUsage? Usage { get; set; }
    public FoundryScenarioError? Error { get; set; }
    public int? ChunkSize { get; set; }
    public int? DelayMs { get; set; }
}

/// <summary>
/// One tool call. Use exactly one of the shorthand fields; they are resolved
/// against the tools the request actually advertises, so one script survives the
/// agent renaming its tools (Codex has shipped <c>shell</c>, <c>local_shell</c>
/// and <c>exec_command</c> for the same thing).
/// </summary>
public sealed class FoundryScenarioCall
{
    /// <summary>A shell command line → <c>exec_command {cmd}</c>, <c>shell {command:[bash,-lc,…]}</c>
    /// or <c>local_shell</c>, whichever is advertised.</summary>
    public string? Shell { get; set; }

    /// <summary>Write a file. Becomes an <c>apply_patch</c> Add File when that tool is
    /// advertised, otherwise a shell heredoc.</summary>
    public FoundryScenarioFile? WriteFile { get; set; }

    /// <summary>A raw <c>*** Begin Patch</c> body for <c>apply_patch</c>.</summary>
    public string? ApplyPatch { get; set; }

    /// <summary>Escape hatch: call a named function with literal arguments. The name
    /// may be <c>$toolMatching:&lt;substring&gt;</c>.</summary>
    public string? Function { get; set; }
    public JsonNode? Arguments { get; set; }
}

public sealed class FoundryScenarioFile
{
    public string Path { get; set; } = "";
    public string Content { get; set; } = "";
}

public sealed class FoundryScenarioUsage
{
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long CachedTokens { get; set; }
    public long ReasoningTokens { get; set; }
}

public sealed class FoundryScenarioError
{
    public int Status { get; set; } = 500;
    public string Message { get; set; } = "Simulated failure";
    public string Code { get; set; } = "server_error";
    public int? RetryAfterSeconds { get; set; }
}

/// <summary>Loads scenarios from a directory (<c>*.json</c>, <c>*.yaml</c>, <c>*.yml</c>)
/// plus the built-ins, and keeps runtime-uploaded ones.</summary>
public sealed class FoundryScenarioStore
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly ConcurrentDictionary<string, FoundryScenario> _scenarios = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _loadErrors = [];

    public FoundryScenarioStore(string? directory)
    {
        foreach (var builtIn in BuiltIns()) _scenarios[builtIn.Name] = builtIn;
        Directory = directory;
        if (!string.IsNullOrWhiteSpace(directory) && System.IO.Directory.Exists(directory))
        {
            foreach (var file in System.IO.Directory.EnumerateFiles(directory)
                         .Where(f => f.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                                  || f.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)
                                  || f.EndsWith(".yml", StringComparison.OrdinalIgnoreCase))
                         .Order(StringComparer.Ordinal))
            {
                try
                {
                    var scenario = Parse(File.ReadAllText(file), file);
                    if (string.IsNullOrWhiteSpace(scenario.Name))
                        scenario.Name = Path.GetFileNameWithoutExtension(file);
                    _scenarios[scenario.Name] = scenario;
                }
                catch (Exception error)
                {
                    lock (_loadErrors) _loadErrors.Add($"{Path.GetFileName(file)}: {error.Message}");
                }
            }
        }
    }

    public string? Directory { get; }
    public IReadOnlyList<string> LoadErrors { get { lock (_loadErrors) return _loadErrors.ToArray(); } }
    public IReadOnlyCollection<FoundryScenario> All => _scenarios.Values.OrderBy(s => s.Name, StringComparer.Ordinal).ToArray();

    public FoundryScenario? Find(string name) => _scenarios.TryGetValue(name, out var s) ? s : null;

    public void Put(FoundryScenario scenario) => _scenarios[scenario.Name] = scenario;

    public bool Remove(string name) => _scenarios.TryRemove(name, out _);

    /// <summary>The first triggered scenario whose regex matches the opening prompt.</summary>
    public (FoundryScenario Scenario, Match Match)? MatchTrigger(string firstUserText, string deployment)
    {
        foreach (var scenario in All)
        {
            if (scenario.Trigger?.PromptRegex is not { Length: > 0 } pattern) continue;
            if (scenario.Trigger.Deployments is { Count: > 0 } only &&
                !only.Contains(deployment, StringComparer.OrdinalIgnoreCase)) continue;
            var match = Regex.Match(firstUserText, pattern, RegexOptions.None, TimeSpan.FromMilliseconds(250));
            if (match.Success) return (scenario, match);
        }
        return null;
    }

    public static FoundryScenario Parse(string text, string? fileName = null)
    {
        var isYaml = fileName is not null &&
                     (fileName.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) ||
                      fileName.EndsWith(".yml", StringComparison.OrdinalIgnoreCase));
        var json = isYaml ? YamlToJson(text) : text;
        return JsonSerializer.Deserialize<FoundryScenario>(json, Json)
            ?? throw new InvalidOperationException("Empty scenario document.");
    }

    /// <summary>YAML → JSON with plain scalars typed the way a human expects
    /// (<c>42</c> is a number, <c>true</c> a boolean, quoted values stay strings).</summary>
    internal static string YamlToJson(string yaml)
    {
        var stream = new YamlStream();
        using var reader = new StringReader(yaml);
        stream.Load(reader);
        if (stream.Documents.Count == 0) return "{}";
        return Convert(stream.Documents[0].RootNode)?.ToJsonString() ?? "{}";

        static JsonNode? Convert(YamlNode node) => node switch
        {
            YamlMappingNode map => new JsonObject(map.Children.Select(kv =>
                KeyValuePair.Create(((YamlScalarNode)kv.Key).Value ?? "", Convert(kv.Value)))),
            YamlSequenceNode seq => new JsonArray(seq.Children.Select(Convert).ToArray()),
            YamlScalarNode scalar => Scalar(scalar),
            _ => null,
        };

        static JsonNode? Scalar(YamlScalarNode scalar)
        {
            var value = scalar.Value;
            if (scalar.Style is not YamlDotNet.Core.ScalarStyle.Plain) return JsonValue.Create(value);
            if (value is null or "~" or "null" or "") return null;
            if (value is "true" or "True") return JsonValue.Create(true);
            if (value is "false" or "False") return JsonValue.Create(false);
            if (long.TryParse(value, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var l)) return JsonValue.Create(l);
            if (double.TryParse(value, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var d)) return JsonValue.Create(d);
            return JsonValue.Create(value);
        }
    }

    private static IEnumerable<FoundryScenario> BuiltIns()
    {
        yield return new FoundryScenario
        {
            Name = "echo",
            Description = "Replies with the last user text, forever.",
            OnExhausted = "last",
            Steps = [new FoundryScenarioStep { Text = "$lastUserText" }],
        };
        yield return new FoundryScenario
        {
            Name = "shell-then-stop",
            Description = "Runs one harmless shell command, then summarises. Smoke test for an agent's tool loop.",
            Steps =
            [
                new FoundryScenarioStep
                {
                    Text = "Checking the working directory first.",
                    Calls = [new FoundryScenarioCall { Shell = "pwd && ls -la" }],
                },
                new FoundryScenarioStep { Text = "Done. The working directory is listed above." },
            ],
        };
        yield return new FoundryScenario
        {
            Name = "rate-limit-then-succeed",
            Description = "A 429 with retry-after, then a normal answer.",
            Steps =
            [
                new FoundryScenarioStep { Error = new FoundryScenarioError { Status = 429, Code = "too_many_requests", Message = "Simulated rate limit", RetryAfterSeconds = 1 } },
                new FoundryScenarioStep { Text = "Recovered after the rate limit." },
            ],
        };
    }
}
