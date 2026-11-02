using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Aspire.Hosting.Foundry.Emulator;

/// <summary>What the playback engine needs to know about a request, independent of
/// whether it arrived as Responses or Chat Completions.</summary>
internal sealed class ConversationView
{
    public required string Deployment { get; init; }
    public required IReadOnlyList<AdvertisedTool> Tools { get; init; }
    public required IReadOnlyList<string> UserTexts { get; init; }

    /// <summary>The step the history says comes next — see <see cref="FoundryScenario"/>.</summary>
    public required int HistoryStep { get; init; }
    public required string SessionKey { get; init; }
    public required long RequestBytes { get; init; }

    /// <summary>The agent's shell and working folder, from Codex's <c>&lt;environment_context&gt;</c>.
    /// Null when the request carries none.</summary>
    public string? Shell { get; init; }
    public string? Cwd { get; init; }

    /// <summary>The newest of the last typed message and the last tool output, for a gate's
    /// <see cref="FoundryScenarioStep.When"/>.</summary>
    public string? LastReply { get; init; }

    /// <summary>Every typed message and tool output, oldest first, for finding which project an
    /// earlier <see cref="FoundryScenarioCall.AskProject"/> was answered with.</summary>
    public IReadOnlyList<string> Replies { get; init; } = [];

    /// <summary>The projects the caller may work on, their own key's first. See <see cref="FoundryProjectChoice"/>.</summary>
    public IReadOnlyList<FoundryProjectChoice> Projects { get; set; } = [];

    /// <summary>The project the conversation picked: the one whose label a reply names, longest label first.</summary>
    public FoundryProjectChoice? Project => Projects
        .OrderByDescending(p => p.Label.Length)
        .FirstOrDefault(p => Replies.Any(r => r.Contains(p.Label, StringComparison.OrdinalIgnoreCase)));

    /// <summary>Codex on Windows runs every command in PowerShell, where a bash heredoc fails.</summary>
    public bool IsPowerShell => Shell is { } s
        && (s.Contains("powershell", StringComparison.OrdinalIgnoreCase) || s.Contains("pwsh", StringComparison.OrdinalIgnoreCase));
}

internal sealed record AdvertisedTool(string Name, string Kind);

/// <summary>A project a caller holds a key for, from that key's <c>context</c>.</summary>
public sealed record FoundryProjectChoice(string Label, string? GitRemote)
{
    /// <summary>The caller's projects: every key with a <c>label</c> held by the same person, their
    /// own key first, then by name. Three at most, which is what <c>request_user_input</c> takes.</summary>
    internal static IReadOnlyList<FoundryProjectChoice> For(FoundryState state, FoundryCaller caller)
    {
        if (string.IsNullOrEmpty(caller.UserPrincipal)) return [];
        static FoundryProjectChoice? Of(FoundryApiKeySeed k) =>
            k.Context?.TryGetValue("label", out var label) == true && label.Length > 0
                ? new FoundryProjectChoice(label, k.Context.TryGetValue("gitRemote", out var remote) ? remote : null)
                : null;
        var keys = state.GatewayKeys.Values
            .Where(k => string.Equals(k.UserPrincipal, caller.UserPrincipal, StringComparison.OrdinalIgnoreCase))
            .OrderBy(k => string.Equals(k.SubscriptionId, caller.SubscriptionId, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(k => k.Name, StringComparer.Ordinal);
        return keys.Select(Of).OfType<FoundryProjectChoice>().DistinctBy(p => p.Label).Take(3).ToList();
    }
}

internal abstract record PlannedItem(string Id);
internal sealed record PlannedReasoning(string Id, string Text) : PlannedItem(Id);
internal sealed record PlannedMessage(string Id, string Text) : PlannedItem(Id);
internal sealed record PlannedFunctionCall(string Id, string CallId, string Name, string Arguments) : PlannedItem(Id);
internal sealed record PlannedCustomCall(string Id, string CallId, string Name, string Input) : PlannedItem(Id);

internal sealed class TurnPlan
{
    public List<PlannedItem> Items { get; } = [];
    public FoundryScenarioError? Error { get; init; }
    public string? Scenario { get; init; }
    public int Step { get; init; }
    public FoundryScenarioUsage? UsageOverride { get; init; }
    public int ChunkSize { get; init; } = 24;
    public int DelayMs { get; init; }
}

/// <summary>
/// Turns (scenario, conversation) into the next assistant turn. Holds one piece of
/// state only: which error steps have already been served to a session, because an
/// error leaves no trace in the history the client re-sends and would otherwise be
/// served forever.
/// </summary>
public sealed class FoundryPlayback(FoundryScenarioStore scenarios)
{
    internal const string CallIdPrefix = "call_sim_";
    private static readonly Regex StepMarker = new(@"^call_sim_s(\d+)_", RegexOptions.Compiled);
    private readonly ConcurrentDictionary<string, byte> _servedErrors = new(StringComparer.Ordinal);

    // How many times a session was answered by a closed gate. Each of those answers is an
    // assistant message in the history the client re-sends, and must not count as a step.
    private readonly ConcurrentDictionary<string, int> _gateWaits = new(StringComparer.Ordinal);

    public FoundryScenarioStore Scenarios => scenarios;

    /// <summary>Directive grammar shared with the agent gateway's Anthropic simulator:
    /// <c>echo</c>, <c>scenario:&lt;name&gt;</c>.</summary>
    internal TurnPlan Plan(ConversationView view, string? directive, string? deploymentScenario, int chunkSize, int delayMs)
    {
        FoundryScenario? scenario = null;
        Match? trigger = null;
        if (!string.IsNullOrWhiteSpace(directive))
        {
            var name = directive.StartsWith("scenario:", StringComparison.OrdinalIgnoreCase) ? directive[9..] : directive;
            scenario = scenarios.Find(name.Trim());
            if (scenario is null)
                return new TurnPlan { Error = new FoundryScenarioError { Status = 404, Code = "scenario_not_found", Message = $"No emulator scenario named '{name}'." } };
        }
        else if (!string.IsNullOrWhiteSpace(deploymentScenario))
        {
            scenario = scenarios.Find(deploymentScenario);
        }

        var firstUser = view.UserTexts.FirstOrDefault() ?? "";
        if (scenario is null && scenarios.MatchTrigger(firstUser, view.Deployment) is { } hit)
        {
            scenario = hit.Scenario;
            trigger = hit.Match;
        }
        else if (scenario?.Trigger?.PromptRegex is { Length: > 0 } pattern)
        {
            var m = Regex.Match(firstUser, pattern);
            if (m.Success) trigger = m;
        }

        if (scenario is null) return Fallback(view, chunkSize, delayMs, null);

        var waitKey = $"{view.SessionKey}|{scenario.Name}";
        var index = view.HistoryStep - (_gateWaits.TryGetValue(waitKey, out var waits) ? waits : 0);
        // Skip error steps this session has already been given.
        while (index < scenario.Steps.Count && scenario.Steps[index].Error is not null &&
               _servedErrors.ContainsKey($"{view.SessionKey}|{scenario.Name}|{index}"))
            index++;

        if (index >= scenario.Steps.Count)
        {
            switch (scenario.OnExhausted.ToLowerInvariant())
            {
                case "last" when scenario.Steps.Count > 0:
                    index = scenario.Steps.Count - 1;
                    break;
                case "error":
                    return new TurnPlan { Scenario = scenario.Name, Step = index, Error = new FoundryScenarioError { Status = 400, Code = "scenario_exhausted", Message = $"Scenario '{scenario.Name}' has no step {index}." } };
                default:
                    return Fallback(view, chunkSize, delayMs, scenario.Name);
            }
        }

        var step = scenario.Steps[index];
        if (step.When is { Length: > 0 } when && !Regex.IsMatch(view.LastReply ?? "", when))
        {
            _gateWaits.AddOrUpdate(waitKey, 1, (_, n) => n + 1);
            step = step.Otherwise is { } otherwise
                ? new FoundryScenarioStep { Text = otherwise.Text, Reasoning = otherwise.Reasoning, Usage = otherwise.Usage }
                : new FoundryScenarioStep { Text = "Jeg venter på dit svar." };
        }

        if (step.Error is { } error)
        {
            _servedErrors[$"{view.SessionKey}|{scenario.Name}|{index}"] = 0;
            return new TurnPlan { Scenario = scenario.Name, Step = index, Error = error };
        }

        var plan = new TurnPlan
        {
            Scenario = scenario.Name,
            Step = index,
            UsageOverride = step.Usage,
            ChunkSize = step.ChunkSize ?? chunkSize,
            DelayMs = step.DelayMs ?? delayMs,
        };

        string Sub(string text) => Substitute(text, view, trigger, index);
        if (!string.IsNullOrEmpty(step.Reasoning))
            plan.Items.Add(new PlannedReasoning(NewId("rs"), Sub(step.Reasoning)));
        if (!string.IsNullOrEmpty(step.Text))
            plan.Items.Add(new PlannedMessage(NewId("msg"), Sub(step.Text)));

        var n = 0;
        foreach (var call in step.Calls)
        {
            var callId = $"{CallIdPrefix}s{index}_{n++}_{RandomNumberGenerator.GetHexString(8, lowercase: true)}";
            plan.Items.Add(ResolveCall(call, view, callId, Sub));
        }
        return plan;
    }

    private static TurnPlan Fallback(ConversationView view, int chunkSize, int delayMs, string? scenario)
    {
        var last = view.UserTexts.LastOrDefault();
        var text = string.IsNullOrWhiteSpace(last)
            ? "This is the Foundry emulator. No scenario matched, so this is a placeholder reply."
            : $"[Foundry emulator · {view.Deployment}] No scenario matched. You said: {Truncate(last, 400)}";
        var plan = new TurnPlan { Scenario = scenario, Step = -1, ChunkSize = chunkSize, DelayMs = delayMs };
        plan.Items.Add(new PlannedMessage(NewId("msg"), text));
        return plan;
    }

    private static PlannedItem ResolveCall(FoundryScenarioCall call, ConversationView view, string callId, Func<string, string> sub)
    {
        var tools = view.Tools;
        AdvertisedTool? Find(params string[] names) =>
            names.Select(n => tools.FirstOrDefault(t => t.Name.Equals(n, StringComparison.OrdinalIgnoreCase)))
                 .FirstOrDefault(t => t is not null);

        if (call.Function is { Length: > 0 } fn)
        {
            var name = fn.StartsWith("$toolMatching:", StringComparison.Ordinal)
                ? tools.FirstOrDefault(t => t.Name.Contains(fn[14..], StringComparison.OrdinalIgnoreCase))?.Name ?? fn[14..]
                : fn;
            var args = call.Arguments is null ? "{}" : sub(call.Arguments.ToJsonString(FoundryDataPlane.Wire));
            var tool = Find(name);
            return tool?.Kind == "custom"
                ? new PlannedCustomCall(NewId("ctc"), callId, name, call.Arguments is JsonValue v && v.TryGetValue<string>(out var s) ? sub(s) : args)
                : new PlannedFunctionCall(NewId("fc"), callId, name, args);
        }

        if (call.AskProject is { } ask)
            return new PlannedFunctionCall(NewId("fc"), callId, Find("request_user_input")?.Name ?? "request_user_input", Json(AskProject(ask, view, sub)));

        if (call.ApplyPatch is { Length: > 0 } patch)
            return Patch(sub(patch));

        if (call.WriteFile is { } file)
        {
            var path = sub(file.Path);
            var content = sub(file.Content);
            if (Find("apply_patch") is not null)
            {
                var body = new StringBuilder();
                body.Append("*** Begin Patch\n*** Add File: ").Append(path).Append('\n');
                foreach (var line in content.Replace("\r\n", "\n").TrimEnd('\n').Split('\n'))
                    body.Append('+').Append(line).Append('\n');
                body.Append("*** End Patch\n");
                return Patch(body.ToString());
            }
            if (view.IsPowerShell) return Shell(PowerShellWrite(path, content));
            var dir = Path.GetDirectoryName(path.Replace('\\', '/'));
            var marker = "FOUNDRY_EMULATOR_EOF";
            var script = (string.IsNullOrEmpty(dir) ? "" : $"mkdir -p '{dir}' && ") +
                         $"cat > '{path}' <<'{marker}'\n{content.TrimEnd('\n')}\n{marker}";
            return Shell(script);
        }

        if (view.IsPowerShell && call.PowerShell is { Length: > 0 } ps)
            return Shell(sub(ps));
        if (call.Shell is { Length: > 0 } command)
            return Shell(sub(command));

        return new PlannedFunctionCall(NewId("fc"), callId, "noop", "{}");

        PlannedItem Patch(string body)
        {
            var tool = Find("apply_patch");
            if (tool?.Kind == "custom") return new PlannedCustomCall(NewId("ctc"), callId, tool.Name, body);
            if (tool is not null) return new PlannedFunctionCall(NewId("fc"), callId, tool.Name, Json(new JsonObject { ["input"] = body }));
            // No apply_patch tool: Codex still intercepts `apply_patch` run through its shell.
            return Shell($"apply_patch <<'PATCH'\n{body.TrimEnd('\n')}\nPATCH");
        }

        PlannedItem Shell(string command)
        {
            if (Find("exec_command") is { } exec)
                return new PlannedFunctionCall(NewId("fc"), callId, exec.Name, Json(new JsonObject { ["cmd"] = command }));
            if (Find("shell", "container.exec", "shell_command") is { } shell)
                return new PlannedFunctionCall(NewId("fc"), callId, shell.Name,
                    shell.Name == "shell_command"
                        ? Json(new JsonObject { ["command"] = command })
                        : Json(new JsonObject { ["command"] = Argv(command) }));
            if (Find("local_shell") is { } local)
                return new PlannedFunctionCall(NewId("fc"), callId, local.Name, Json(new JsonObject { ["command"] = Argv(command) }));
            return new PlannedFunctionCall(NewId("fc"), callId, "exec_command", Json(new JsonObject { ["cmd"] = command }));
        }

        JsonArray Argv(string command) => view.IsPowerShell
            ? new JsonArray("powershell.exe", "-NoProfile", "-Command", command)
            : new JsonArray("bash", "-lc", command);
    }

    /// <summary>
    /// A file write PowerShell runs. The content goes in a literal here-string, so it reads like the
    /// file in the agent's transcript; a line that would close the here-string early falls back to
    /// base64. WriteAllText writes UTF-8 without a BOM, which Set-Content in Windows PowerShell does not.
    /// </summary>
    internal static string PowerShellWrite(string path, string content)
    {
        static string Quote(string s) => "'" + s.Replace("'", "''") + "'";
        var normalized = content.Replace("\r\n", "\n").TrimEnd('\n') + "\n";
        var dir = Path.GetDirectoryName(path.Replace('\\', '/'));
        var mkdir = string.IsNullOrEmpty(dir) ? "" : $"New-Item -ItemType Directory -Force -Path {Quote(dir)} | Out-Null\n";
        var target = $"(Join-Path (Get-Location) {Quote(path)})";
        if (normalized.Split('\n').Any(l => l.StartsWith("'@", StringComparison.Ordinal)))
        {
            var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(normalized));
            return mkdir + $"[System.IO.File]::WriteAllBytes({target}, [Convert]::FromBase64String('{b64}'))";
        }
        return mkdir + $"[System.IO.File]::WriteAllText({target}, @'\n{normalized}'@)";
    }

    private static string Json(JsonNode node) => node.ToJsonString(FoundryDataPlane.Wire);

    // request_user_input wants two or three options. With one project the second is a way out, with
    // none the question still asks, so the scenario's gate can take a typed answer instead.
    private static JsonObject AskProject(FoundryScenarioAsk ask, ConversationView view, Func<string, string> sub)
    {
        var options = new JsonArray();
        foreach (var p in view.Projects)
            options.Add(new JsonObject { ["label"] = p.Label, ["description"] = sub(ask.OptionDescription) });
        if (options.Count < 2)
            options.Add(new JsonObject { ["label"] = sub(ask.Other), ["description"] = sub(ask.OtherDescription) });
        if (options.Count < 2)
            options.Add(new JsonObject { ["label"] = "Skip", ["description"] = "Answer in the chat instead." });
        return new JsonObject
        {
            ["questions"] = new JsonArray(new JsonObject
            {
                ["id"] = ask.Id,
                ["header"] = sub(ask.Header),
                ["question"] = sub(ask.Question),
                ["options"] = options,
            }),
        };
    }

    private static string Substitute(string text, ConversationView view, Match? trigger, int step)
    {
        var first = view.UserTexts.FirstOrDefault() ?? "";
        var prompt = trigger is { Groups.Count: > 1 } && trigger.Groups[1].Success ? trigger.Groups[1].Value.Trim() : first;
        return text
            .Replace("$lastUserText", view.UserTexts.LastOrDefault() ?? "", StringComparison.Ordinal)
            .Replace("$firstUserText", first, StringComparison.Ordinal)
            .Replace("$prompt", prompt, StringComparison.Ordinal)
            .Replace("$slug", Slug(prompt), StringComparison.Ordinal)
            .Replace("$model", view.Deployment, StringComparison.Ordinal)
            .Replace("$step", step.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("$cwdUrl", FileUrl(view.Cwd), StringComparison.Ordinal)
            .Replace("$projectRemote", view.Project?.GitRemote ?? "", StringComparison.Ordinal)
            .Replace("$projectName", view.Project?.Label ?? "", StringComparison.Ordinal);
    }

    /// <summary>The working folder as a <c>file://</c> URL, which Codex's chat links open. Empty when unknown.</summary>
    internal static string FileUrl(string? cwd)
    {
        if (string.IsNullOrWhiteSpace(cwd)) return "";
        var path = cwd.Trim().Replace('\\', '/').TrimEnd('/');
        var encoded = string.Join('/', path.Split('/').Select(Uri.EscapeDataString)).Replace("%3A", ":", StringComparison.Ordinal);
        return path.StartsWith('/') ? "file://" + encoded : "file:///" + encoded;
    }

    internal static string Slug(string text)
    {
        var slug = Regex.Replace(text.ToLowerInvariant(), "[^a-z0-9æøå]+", "-").Trim('-');
        slug = slug.Replace("æ", "ae").Replace("ø", "oe").Replace("å", "aa");
        return slug.Length == 0 ? "app" : slug.Length > 40 ? slug[..40].TrimEnd('-') : slug;
    }

    internal static string NewId(string prefix) => $"{prefix}_{RandomNumberGenerator.GetHexString(24, lowercase: true)}";

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max] + "…";

    /// <summary>The step implied by a history: each assistant message is one turn,
    /// and a tool call stamped by this emulator says exactly which step produced it.</summary>
    internal static int HistoryStepFrom(IEnumerable<(string Kind, string? CallId)> history)
    {
        var index = 0;
        foreach (var (kind, callId) in history)
        {
            if (kind == "assistant") index++;
            else if (kind == "call" && callId is not null && StepMarker.Match(callId) is { Success: true } m &&
                     int.TryParse(m.Groups[1].Value, out var stepped))
                index = Math.Max(index, stepped + 1);
            else if (kind == "call") index++;
        }
        return index;
    }

    internal static string SessionKeyFrom(string? explicitKey, IEnumerable<string> userTexts)
    {
        if (!string.IsNullOrWhiteSpace(explicitKey)) return explicitKey;
        var first = userTexts.FirstOrDefault() ?? "";
        return "h:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(first)))[..16];
    }
}
