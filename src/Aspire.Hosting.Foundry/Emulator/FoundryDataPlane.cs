using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Aspire.Hosting.Foundry.Emulator;

/// <summary>
/// The OpenAI-compatible inference surface, in the shapes Foundry exposes it:
/// <list type="bullet">
/// <item><c>/openai/v1/responses</c> and <c>/openai/v1/chat/completions</c> — the v1 routes,
/// deployment name in <c>model</c>, no <c>api-version</c>. What Codex and the OpenAI SDKs speak.</item>
/// <item><c>/openai/deployments/{deployment}/chat/completions?api-version=…</c> — the dated
/// Azure route, deployment in the path.</item>
/// </list>
/// Each is also served under <c>/{account}/…</c> (a stand-in for the account's own host
/// name) and <c>/{account}/api/projects/{project}/…</c> (a Foundry project endpoint).
/// </summary>
internal static class FoundryDataPlane
{
    /// <summary>Keep æøå and markup readable on the wire, as the real service does.</summary>
    internal static readonly JsonSerializerOptions Wire = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static readonly string[] Prefixes = ["", "/{account}", "/{account}/api/projects/{project}"];

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        foreach (var prefix in Prefixes)
        {
            // Cast to Delegate: a bare HttpContext lambda binds as a RequestDelegate and
            // would silently discard the IResult.
            endpoints.MapPost(prefix + "/openai/v1/responses", (Delegate)((HttpContext c) => Handle(c, Api.Responses, null, requireVersion: false)));
            endpoints.MapPost(prefix + "/openai/responses", (Delegate)((HttpContext c) => Handle(c, Api.Responses, null, requireVersion: true)));
            endpoints.MapGet(prefix + "/openai/v1/responses/{id}", GetResponse);
            endpoints.MapPost(prefix + "/openai/v1/chat/completions", (Delegate)((HttpContext c) => Handle(c, Api.Chat, null, requireVersion: false)));
            endpoints.MapPost(prefix + "/openai/deployments/{deployment}/chat/completions",
                (Delegate)((HttpContext c, string deployment) => Handle(c, Api.Chat, deployment, requireVersion: true)));
            endpoints.MapGet(prefix + "/openai/v1/models", ListModels);
            endpoints.MapGet(prefix + "/openai/models", ListModels);
        }
    }

    private enum Api { Responses, Chat }

    private static IResult Error(int status, string code, string message, string? type = null) =>
        Results.Json(new { error = new { code, message, type, param = (string?)null } }, statusCode: status);

    private static (FoundryAccount? Account, IResult? Error) ResolveAccount(HttpContext context, FoundryState state, string? deployment)
    {
        var name = context.Request.RouteValues["account"] as string;
        if (name is null) return (state.DefaultAccountFor(deployment), null);
        var account = state.FindAccount(name);
        return account is null
            ? (null, Error(404, "ResourceNotFound", $"The Foundry account '{name}' does not exist in this emulator."))
            : (account, null);
    }

    private static async Task<IResult> ListModels(HttpContext context, FoundryState state, FoundryAuthenticator auth)
    {
        var (account, failure) = ResolveAccount(context, state, null);
        if (failure is not null) return failure;
        if (account is null) return Error(404, "ResourceNotFound", "No Foundry account is seeded.");
        var (caller, denied) = await auth.AuthenticateAsync(context.Request, account, context.RequestAborted);
        if (caller is null) return Error(401, "401", denied!);
        var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return Results.Json(new
        {
            @object = "list",
            data = account.Deployments.Values.OrderBy(d => d.Name).Select(d => new
            {
                id = d.Name,
                @object = "model",
                created,
                owned_by = d.ModelFormat,
                model = d.ModelName,
            }),
        });
    }

    private static IResult GetResponse(string id, FoundryResponseCache cache) =>
        cache.TryGet(id, out var stored)
            ? Results.Content(stored.ToJsonString(FoundryDataPlane.Wire), "application/json")
            : Error(404, "not_found", $"Response '{id}' not found. Responses are kept in memory only, and only when store != false.");

    private static async Task<IResult> Handle(HttpContext context, Api api, string? pathDeployment, bool requireVersion)
    {
        var services = context.RequestServices;
        var state = (FoundryState)services.GetService(typeof(FoundryState))!;
        var auth = (FoundryAuthenticator)services.GetService(typeof(FoundryAuthenticator))!;
        var playback = (FoundryPlayback)services.GetService(typeof(FoundryPlayback))!;
        var usage = (FoundryUsageStore)services.GetService(typeof(FoundryUsageStore))!;
        var cache = (FoundryResponseCache)services.GetService(typeof(FoundryResponseCache))!;
        var request = context.Request;

        if (requireVersion && string.IsNullOrEmpty(request.Query["api-version"]))
            return Error(400, "BadRequest", "Missing required query parameter: api-version.");

        using var buffer = new MemoryStream();
        await request.Body.CopyToAsync(buffer, context.RequestAborted);
        var bytes = buffer.ToArray();
        JsonObject body;
        try
        {
            body = JsonNode.Parse(bytes) as JsonObject ?? throw new JsonException("Body is not a JSON object.");
        }
        catch (JsonException error)
        {
            return Error(400, "invalid_request_error", $"Could not parse the request body: {error.Message}");
        }

        var deployment = pathDeployment ?? body["model"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(deployment))
            return Error(400, "invalid_request_error", "Missing required parameter: 'model'.");

        var (account, failure) = ResolveAccount(context, state, deployment);
        if (failure is not null) return failure;
        if (account is null) return Error(404, "DeploymentNotFound", "No Foundry account is seeded in this emulator.");

        var (caller, denied) = await auth.AuthenticateAsync(request, account, context.RequestAborted);
        if (caller is null)
            return Error(401, "401", denied!);

        if (account.FindDeployment(deployment) is not { } dep)
            return Error(404, "DeploymentNotFound",
                $"The API deployment '{deployment}' for this resource does not exist. If you created the deployment within the last 5 minutes, please wait a moment and try again.");

        // Scenario authoring aid: FOUNDRY_REQUEST_LOG_DIR keeps every request body, so
        // the exact tools and history an agent sends can be read instead of guessed.
        if (Environment.GetEnvironmentVariable("FOUNDRY_REQUEST_LOG_DIR") is { Length: > 0 } logDir)
        {
            Directory.CreateDirectory(logDir);
            await File.WriteAllBytesAsync(Path.Combine(logDir,
                $"{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfff}-{(api == Api.Responses ? "responses" : "chat")}.json"), bytes, context.RequestAborted);
        }

        var stream = body["stream"]?.GetValueKind() == JsonValueKind.True;
        var view = api == Api.Responses
            ? FoundryRequestReader.ReadResponses(body, deployment, bytes.LongLength, cache, request)
            : FoundryRequestReader.ReadChat(body, deployment, bytes.LongLength, request);

        var directive = caller.SimDirective
            ?? Header(request, "x-foundry-sim") ?? Header(request, "x-gateway-sim");
        var plan = playback.Plan(view, directive, dep.Scenario,
            Math.Max(1, state.Behaviour.DefaultChunkSize), Math.Max(0, state.Behaviour.DefaultDelayMs));

        var project = Header(request, "x-foundry-project") ?? Header(request, "x-project-id")
            ?? Meta(body, "project") ?? Meta(body, "project_id")
            ?? request.RouteValues["project"] as string ?? caller.Project;
        var customer = Header(request, "x-foundry-customer") ?? Header(request, "x-customer-id")
            ?? Meta(body, "customer") ?? Meta(body, "customer_id") ?? caller.Customer;
        var endUser = body["safety_identifier"]?.GetValue<string>() ?? body["user"]?.GetValue<string>();
        var agent = Header(request, "originator") ?? request.Headers.UserAgent.ToString().Split(' ', '/').FirstOrDefault();

        void Record(int status, long input, long cached, long output, long reasoning) => usage.Add(new FoundryUsageRecord(
            Id: FoundryPlayback.NewId("use"),
            Timestamp: DateTimeOffset.UtcNow,
            Account: account.Name,
            Deployment: dep.Name,
            Model: dep.ModelName,
            Operation: api == Api.Responses ? "responses" : "chat.completions",
            Caller: caller.Id,
            CallerType: caller.Type,
            Customer: customer,
            Project: project,
            EndUser: endUser,
            Agent: string.IsNullOrWhiteSpace(agent) ? null : agent,
            InputTokens: input,
            CachedInputTokens: cached,
            OutputTokens: output,
            ReasoningTokens: reasoning,
            StatusCode: status,
            Stream: stream,
            Scenario: plan.Scenario,
            ScenarioStep: plan.Step < 0 ? null : plan.Step,
            SubscriptionId: caller.SubscriptionId,
            UserPrincipal: caller.UserPrincipal,
            Initiative: Header(request, "x-foundry-initiative") ?? Meta(body, "initiative") ?? caller.Initiative,
            Line: request.RouteValues["account"] is string routed && !routed.Equals(account.Name, StringComparison.OrdinalIgnoreCase) ? routed : null));

        if (plan.Error is { } simulated)
        {
            Record(simulated.Status, 0, 0, 0, 0);
            if (simulated.RetryAfterSeconds is { } retry)
            {
                context.Response.Headers["retry-after"] = retry.ToString(CultureInfo.InvariantCulture);
                context.Response.Headers["retry-after-ms"] = (retry * 1000).ToString(CultureInfo.InvariantCulture);
            }
            return Error(simulated.Status, simulated.Code, simulated.Message);
        }

        var tokens = TokenMeter.Measure(plan, view, cache);
        Record(200, tokens.Input, tokens.Cached, tokens.Output, tokens.Reasoning);
        context.Response.Headers["x-ms-region"] = account.Location;
        context.Response.Headers["x-foundry-emulator-scenario"] = plan.Scenario ?? "(fallback)";
        context.Response.Headers["apim-request-id"] = Guid.NewGuid().ToString();

        if (api == Api.Responses)
        {
            var store = body["store"]?.GetValueKind() != JsonValueKind.False;
            var writer = new ResponsesWriter(plan, dep, tokens, body);
            if (store) cache.Remember(writer.ResponseId, writer.Final(), view);
            if (!stream) return Results.Content(writer.Final().ToJsonString(FoundryDataPlane.Wire), "application/json");
            await writer.StreamAsync(context);
            return Results.Empty;
        }
        else
        {
            var writer = new ChatWriter(plan, dep, tokens);
            if (!stream) return Results.Content(writer.Final().ToJsonString(FoundryDataPlane.Wire), "application/json");
            var includeUsage = body["stream_options"]?["include_usage"]?.GetValueKind() == JsonValueKind.True;
            await writer.StreamAsync(context, includeUsage);
            return Results.Empty;
        }
    }

    private static string? Header(HttpRequest request, string name) =>
        request.Headers.TryGetValue(name, out var v) && !string.IsNullOrWhiteSpace(v.ToString()) ? v.ToString() : null;

    private static string? Meta(JsonObject body, string key) =>
        body["metadata"] is JsonObject meta && meta[key] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : null;
}

internal readonly record struct TokenCount(long Input, long Cached, long Output, long Reasoning)
{
    public long Total => Input + Output;
}

/// <summary>
/// Deterministic token contract, so a test can assert usage without a tokenizer:
/// <c>input = max(1, requestBodyBytes / 4)</c>, <c>output = max(1, emittedChars / 4)</c>,
/// <c>cached = the previous input of the same session</c> (capped at this input),
/// which is roughly what prompt caching does to an agent that re-sends its history.
/// A scenario step's <c>usage</c> overrides any non-zero field.
/// </summary>
internal static class TokenMeter
{
    public static TokenCount Measure(TurnPlan plan, ConversationView view, FoundryResponseCache cache)
    {
        long chars = 0, reasoningChars = 0;
        foreach (var item in plan.Items)
        {
            switch (item)
            {
                case PlannedMessage m: chars += m.Text.Length; break;
                case PlannedReasoning r: reasoningChars += r.Text.Length; break;
                case PlannedFunctionCall f: chars += f.Name.Length + f.Arguments.Length; break;
                case PlannedCustomCall c: chars += c.Name.Length + c.Input.Length; break;
            }
        }
        var input = Math.Max(1, view.RequestBytes / 4);
        var reasoning = reasoningChars / 4;
        var output = Math.Max(1, (chars + reasoningChars) / 4);
        var cached = Math.Min(cache.PreviousInput(view.SessionKey, input), input);
        cached -= cached % 64;

        if (plan.UsageOverride is { } o)
        {
            if (o.InputTokens > 0) input = o.InputTokens;
            if (o.OutputTokens > 0) output = o.OutputTokens;
            if (o.CachedTokens > 0) cached = Math.Min(o.CachedTokens, input);
            if (o.ReasoningTokens > 0) reasoning = Math.Min(o.ReasoningTokens, output);
        }
        return new TokenCount(input, cached, output, reasoning);
    }
}

/// <summary>Stored responses for <c>previous_response_id</c> and <c>GET /responses/{id}</c>,
/// plus the per-session input size used for the cached-token estimate. Bounded.</summary>
public sealed class FoundryResponseCache
{
    private const int Capacity = 512;
    private readonly ConcurrentDictionary<string, (JsonObject Response, List<(string Kind, string? CallId)> History, List<string> UserTexts)> _responses = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _order = new();
    private readonly ConcurrentDictionary<string, long> _lastInput = new(StringComparer.Ordinal);

    internal void Remember(string id, JsonObject response, ConversationView view)
    {
        var history = new List<(string, string?)>();
        // The response's own output becomes part of the history of whatever chains on it.
        foreach (var item in response["output"]?.AsArray() ?? [])
        {
            var type = item?["type"]?.GetValue<string>();
            if (type == "message") history.Add(("assistant", null));
            else if (type is "function_call" or "custom_tool_call") history.Add(("call", item?["call_id"]?.GetValue<string>()));
        }
        _responses[id] = (response, history, view.UserTexts.ToList());
        _order.Enqueue(id);
        while (_order.Count > Capacity && _order.TryDequeue(out var old)) _responses.TryRemove(old, out _);
    }

    internal bool TryGet(string id, out JsonObject response)
    {
        if (_responses.TryGetValue(id, out var entry)) { response = entry.Response; return true; }
        response = null!;
        return false;
    }

    internal (List<(string Kind, string? CallId)> History, List<string> UserTexts)? Chain(string id) =>
        _responses.TryGetValue(id, out var e) ? (e.History, e.UserTexts) : null;

    internal long PreviousInput(string session, long current)
    {
        var previous = _lastInput.TryGetValue(session, out var p) ? p : 0;
        _lastInput[session] = current;
        return previous;
    }
}

internal static class FoundryRequestReader
{
    /// <summary>Codex and friends wrap context (<c>&lt;environment_context&gt;</c>,
    /// <c>&lt;user_instructions&gt;</c>, AGENTS.md) in user-role messages. Those are
    /// not what the person typed, so they never trigger or substitute.</summary>
    private static bool IsTypedByHuman(string text)
    {
        var t = text.TrimStart();
        return t.Length > 0 && !t.StartsWith('<') && !t.StartsWith("# AGENTS.md", StringComparison.Ordinal);
    }

    public static ConversationView ReadResponses(JsonObject body, string deployment, long bytes, FoundryResponseCache cache, HttpRequest request)
    {
        var history = new List<(string Kind, string? CallId)>();
        var users = new List<string>();
        if (body["previous_response_id"]?.GetValue<string>() is { Length: > 0 } previous && cache.Chain(previous) is { } chain)
        {
            history.AddRange(chain.History);
            users.AddRange(chain.UserTexts);
        }

        switch (body["input"])
        {
            case JsonValue v when v.TryGetValue<string>(out var s):
                if (IsTypedByHuman(s)) users.Add(s);
                break;
            case JsonArray items:
                foreach (var item in items.OfType<JsonObject>())
                {
                    var type = item["type"]?.GetValue<string>() ?? "message";
                    switch (type)
                    {
                        case "message":
                            var role = item["role"]?.GetValue<string>();
                            var text = Text(item["content"]);
                            if (role == "user" && IsTypedByHuman(text)) users.Add(text);
                            else if (role == "assistant") history.Add(("assistant", null));
                            break;
                        case "function_call" or "custom_tool_call" or "local_shell_call":
                            history.Add(("call", item["call_id"]?.GetValue<string>()));
                            break;
                    }
                }
                break;
        }

        var tools = new List<AdvertisedTool>();
        foreach (var tool in body["tools"]?.AsArray().OfType<JsonObject>() ?? [])
        {
            var type = tool["type"]?.GetValue<string>();
            var name = tool["name"]?.GetValue<string>();
            switch (type)
            {
                case "function" when name is not null: tools.Add(new(name, "function")); break;
                case "custom" when name is not null: tools.Add(new(name, "custom")); break;
                case "local_shell": tools.Add(new("local_shell", "local_shell")); break;
            }
        }

        var explicitSession = request.Headers["x-foundry-sim-session"].ToString() is { Length: > 0 } h ? h
            : body["prompt_cache_key"]?.GetValue<string>() is { Length: > 0 } k ? k
            : request.Headers["session-id"].ToString() is { Length: > 0 } sid ? sid
            : null;

        return new ConversationView
        {
            Deployment = deployment,
            Tools = tools,
            UserTexts = users,
            HistoryStep = FoundryPlayback.HistoryStepFrom(history),
            SessionKey = FoundryPlayback.SessionKeyFrom(explicitSession, users),
            RequestBytes = bytes,
        };
    }

    public static ConversationView ReadChat(JsonObject body, string deployment, long bytes, HttpRequest request)
    {
        var history = new List<(string Kind, string? CallId)>();
        var users = new List<string>();
        foreach (var message in body["messages"]?.AsArray().OfType<JsonObject>() ?? [])
        {
            var role = message["role"]?.GetValue<string>();
            if (role == "user")
            {
                var text = Text(message["content"]);
                if (IsTypedByHuman(text)) users.Add(text);
            }
            else if (role == "assistant")
            {
                var firstCall = message["tool_calls"]?.AsArray().OfType<JsonObject>().FirstOrDefault()?["id"]?.GetValue<string>();
                history.Add(firstCall is null ? ("assistant", null) : ("call", firstCall));
            }
        }

        var tools = (body["tools"]?.AsArray().OfType<JsonObject>() ?? [])
            .Select(t => t["function"]?["name"]?.GetValue<string>())
            .Where(n => n is not null)
            .Select(n => new AdvertisedTool(n!, "function"))
            .ToList();

        var explicitSession = request.Headers["x-foundry-sim-session"].ToString() is { Length: > 0 } h ? h
            : body["user"]?.GetValue<string>() is { Length: > 0 } u ? "user:" + u + ":" + (users.FirstOrDefault() ?? "").GetHashCode()
            : null;

        return new ConversationView
        {
            Deployment = deployment,
            Tools = tools,
            UserTexts = users,
            HistoryStep = FoundryPlayback.HistoryStepFrom(history),
            SessionKey = FoundryPlayback.SessionKeyFrom(explicitSession, users),
            RequestBytes = bytes,
        };
    }

    private static string Text(JsonNode? content) => content switch
    {
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonArray parts => string.Join("\n", parts.OfType<JsonObject>()
            .Select(p => p["text"]?.GetValue<string>())
            .Where(t => t is not null)),
        _ => "",
    };
}

/// <summary>Responses API output, as one JSON object or as the SSE event sequence
/// (<c>response.created</c> → items → <c>response.completed</c>).</summary>
internal sealed class ResponsesWriter(TurnPlan plan, FoundryDeploymentSeed deployment, TokenCount tokens, JsonObject request)
{
    private int _sequence;
    public string ResponseId { get; } = FoundryPlayback.NewId("resp");
    private readonly long _created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private JsonObject Envelope(string status, JsonArray output, bool withUsage) => new()
    {
        ["id"] = ResponseId,
        ["object"] = "response",
        ["created_at"] = _created,
        ["status"] = status,
        ["background"] = false,
        ["completed_at"] = status == "completed" ? DateTimeOffset.UtcNow.ToUnixTimeSeconds() : null,
        ["error"] = null,
        ["incomplete_details"] = null,
        ["instructions"] = request["instructions"]?.DeepClone(),
        ["max_output_tokens"] = request["max_output_tokens"]?.DeepClone(),
        ["model"] = deployment.Name,
        ["output"] = output,
        ["parallel_tool_calls"] = request["parallel_tool_calls"]?.DeepClone() ?? true,
        ["previous_response_id"] = request["previous_response_id"]?.DeepClone(),
        ["reasoning"] = request["reasoning"]?.DeepClone() ?? new JsonObject { ["effort"] = null, ["summary"] = null },
        ["store"] = request["store"]?.DeepClone() ?? true,
        ["temperature"] = 1.0,
        ["text"] = new JsonObject { ["format"] = new JsonObject { ["type"] = "text" } },
        ["tool_choice"] = request["tool_choice"]?.DeepClone() ?? "auto",
        ["tools"] = new JsonArray(),
        ["top_p"] = 1.0,
        ["truncation"] = "disabled",
        ["usage"] = withUsage ? Usage() : null,
        ["user"] = request["user"]?.DeepClone(),
        ["metadata"] = request["metadata"]?.DeepClone() ?? new JsonObject(),
    };

    private JsonObject Usage() => new()
    {
        ["input_tokens"] = tokens.Input,
        ["input_tokens_details"] = new JsonObject { ["cached_tokens"] = tokens.Cached },
        ["output_tokens"] = tokens.Output,
        ["output_tokens_details"] = new JsonObject { ["reasoning_tokens"] = tokens.Reasoning },
        ["total_tokens"] = tokens.Total,
    };

    private static JsonObject Item(PlannedItem item, bool done) => item switch
    {
        PlannedMessage m => new JsonObject
        {
            ["id"] = m.Id, ["type"] = "message", ["status"] = done ? "completed" : "in_progress", ["role"] = "assistant",
            ["content"] = done
                ? new JsonArray(new JsonObject { ["type"] = "output_text", ["text"] = m.Text, ["annotations"] = new JsonArray(), ["logprobs"] = new JsonArray() })
                : new JsonArray(),
        },
        PlannedReasoning r => new JsonObject
        {
            ["id"] = r.Id, ["type"] = "reasoning",
            ["summary"] = done ? new JsonArray(new JsonObject { ["type"] = "summary_text", ["text"] = r.Text }) : new JsonArray(),
        },
        PlannedFunctionCall f => new JsonObject
        {
            ["id"] = f.Id, ["type"] = "function_call", ["status"] = done ? "completed" : "in_progress",
            ["call_id"] = f.CallId, ["name"] = f.Name, ["arguments"] = done ? f.Arguments : "",
        },
        PlannedCustomCall c => new JsonObject
        {
            ["id"] = c.Id, ["type"] = "custom_tool_call", ["status"] = done ? "completed" : "in_progress",
            ["call_id"] = c.CallId, ["name"] = c.Name, ["input"] = done ? c.Input : "",
        },
        _ => throw new InvalidOperationException(),
    };

    public JsonObject Final() => Envelope("completed", new JsonArray(plan.Items.Select(i => (JsonNode)Item(i, true)).ToArray()), true);

    public async Task StreamAsync(HttpContext context)
    {
        var response = context.Response;
        response.StatusCode = 200;
        response.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache";
        var ct = context.RequestAborted;

        await Send(response, "response.created", new JsonObject { ["response"] = Envelope("in_progress", [], false) }, ct);
        await Send(response, "response.in_progress", new JsonObject { ["response"] = Envelope("in_progress", [], false) }, ct);

        for (var index = 0; index < plan.Items.Count; index++)
        {
            var item = plan.Items[index];
            await Send(response, "response.output_item.added", new JsonObject { ["output_index"] = index, ["item"] = Item(item, false) }, ct);
            switch (item)
            {
                case PlannedMessage m:
                    var part = new JsonObject { ["type"] = "output_text", ["text"] = "", ["annotations"] = new JsonArray(), ["logprobs"] = new JsonArray() };
                    await Send(response, "response.content_part.added", new JsonObject { ["item_id"] = m.Id, ["output_index"] = index, ["content_index"] = 0, ["part"] = part }, ct);
                    foreach (var chunk in Chunks(m.Text))
                    {
                        await Send(response, "response.output_text.delta", new JsonObject { ["item_id"] = m.Id, ["output_index"] = index, ["content_index"] = 0, ["delta"] = chunk, ["logprobs"] = new JsonArray() }, ct);
                        await Pace(ct);
                    }
                    await Send(response, "response.output_text.done", new JsonObject { ["item_id"] = m.Id, ["output_index"] = index, ["content_index"] = 0, ["text"] = m.Text, ["logprobs"] = new JsonArray() }, ct);
                    await Send(response, "response.content_part.done", new JsonObject
                    {
                        ["item_id"] = m.Id, ["output_index"] = index, ["content_index"] = 0,
                        ["part"] = new JsonObject { ["type"] = "output_text", ["text"] = m.Text, ["annotations"] = new JsonArray(), ["logprobs"] = new JsonArray() },
                    }, ct);
                    break;
                case PlannedReasoning r:
                    var summaryPart = new JsonObject { ["type"] = "summary_text", ["text"] = "" };
                    await Send(response, "response.reasoning_summary_part.added", new JsonObject { ["item_id"] = r.Id, ["output_index"] = index, ["summary_index"] = 0, ["part"] = summaryPart }, ct);
                    foreach (var chunk in Chunks(r.Text))
                    {
                        await Send(response, "response.reasoning_summary_text.delta", new JsonObject { ["item_id"] = r.Id, ["output_index"] = index, ["summary_index"] = 0, ["delta"] = chunk }, ct);
                        await Pace(ct);
                    }
                    await Send(response, "response.reasoning_summary_text.done", new JsonObject { ["item_id"] = r.Id, ["output_index"] = index, ["summary_index"] = 0, ["text"] = r.Text }, ct);
                    await Send(response, "response.reasoning_summary_part.done", new JsonObject
                    {
                        ["item_id"] = r.Id, ["output_index"] = index, ["summary_index"] = 0,
                        ["part"] = new JsonObject { ["type"] = "summary_text", ["text"] = r.Text },
                    }, ct);
                    break;
                case PlannedFunctionCall f:
                    foreach (var chunk in Chunks(f.Arguments))
                        await Send(response, "response.function_call_arguments.delta", new JsonObject { ["item_id"] = f.Id, ["output_index"] = index, ["delta"] = chunk }, ct);
                    await Send(response, "response.function_call_arguments.done", new JsonObject { ["item_id"] = f.Id, ["output_index"] = index, ["arguments"] = f.Arguments }, ct);
                    break;
                case PlannedCustomCall c:
                    foreach (var chunk in Chunks(c.Input))
                        await Send(response, "response.custom_tool_call_input.delta", new JsonObject { ["item_id"] = c.Id, ["output_index"] = index, ["delta"] = chunk }, ct);
                    await Send(response, "response.custom_tool_call_input.done", new JsonObject { ["item_id"] = c.Id, ["output_index"] = index, ["input"] = c.Input }, ct);
                    break;
            }
            await Send(response, "response.output_item.done", new JsonObject { ["output_index"] = index, ["item"] = Item(item, true) }, ct);
        }

        await Send(response, "response.completed", new JsonObject { ["response"] = Final() }, ct);
    }

    private IEnumerable<string> Chunks(string text)
    {
        var size = Math.Max(1, plan.ChunkSize);
        for (var i = 0; i < text.Length; i += size)
            yield return text.Substring(i, Math.Min(size, text.Length - i));
    }

    private Task Pace(CancellationToken ct) => plan.DelayMs > 0 ? Task.Delay(plan.DelayMs, ct) : Task.CompletedTask;

    private async Task Send(HttpResponse response, string type, JsonObject payload, CancellationToken ct)
    {
        var data = new JsonObject { ["type"] = type, ["sequence_number"] = _sequence++ };
        foreach (var (key, value) in payload) data[key] = value?.DeepClone();
        await response.WriteAsync($"event: {type}\ndata: {data.ToJsonString(FoundryDataPlane.Wire)}\n\n", Encoding.UTF8, ct);
        await response.Body.FlushAsync(ct);
    }
}

/// <summary>Chat Completions output — <c>chat.completion</c> or a <c>chat.completion.chunk</c> stream ending in <c>[DONE]</c>.</summary>
internal sealed class ChatWriter(TurnPlan plan, FoundryDeploymentSeed deployment, TokenCount tokens)
{
    private readonly string _id = "chatcmpl-" + FoundryPlayback.NewId("x")[2..];
    private readonly long _created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private string Text => string.Join("\n\n", plan.Items.OfType<PlannedMessage>().Select(m => m.Text));

    private IEnumerable<(string Id, string Name, string Arguments)> Calls => plan.Items.Select(i => i switch
    {
        PlannedFunctionCall f => (f.CallId, f.Name, f.Arguments),
        PlannedCustomCall c => (c.CallId, c.Name, new JsonObject { ["input"] = c.Input }.ToJsonString(FoundryDataPlane.Wire)),
        _ => default,
    }).Where(c => c.Item1 is not null);

    private string FinishReason => Calls.Any() ? "tool_calls" : "stop";

    private JsonObject Usage() => new()
    {
        ["prompt_tokens"] = tokens.Input,
        ["completion_tokens"] = tokens.Output,
        ["total_tokens"] = tokens.Total,
        ["prompt_tokens_details"] = new JsonObject { ["cached_tokens"] = tokens.Cached },
        ["completion_tokens_details"] = new JsonObject { ["reasoning_tokens"] = tokens.Reasoning },
    };

    public JsonObject Final()
    {
        var message = new JsonObject { ["role"] = "assistant", ["content"] = Text.Length == 0 ? null : Text, ["refusal"] = null };
        var calls = Calls.ToList();
        if (calls.Count > 0)
            message["tool_calls"] = new JsonArray(calls.Select(c => (JsonNode)new JsonObject
            {
                ["id"] = c.Id, ["type"] = "function",
                ["function"] = new JsonObject { ["name"] = c.Name, ["arguments"] = c.Arguments },
            }).ToArray());
        return new JsonObject
        {
            ["id"] = _id,
            ["object"] = "chat.completion",
            ["created"] = _created,
            ["model"] = deployment.ModelName,
            ["system_fingerprint"] = "fp_foundry_emulator",
            ["choices"] = new JsonArray(new JsonObject
            {
                ["index"] = 0, ["message"] = message, ["finish_reason"] = FinishReason, ["logprobs"] = null,
            }),
            ["usage"] = Usage(),
        };
    }

    public async Task StreamAsync(HttpContext context, bool includeUsage)
    {
        var response = context.Response;
        response.StatusCode = 200;
        response.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache";
        var ct = context.RequestAborted;

        async Task Chunk(JsonObject delta, string? finish, JsonObject? usage = null, bool noChoices = false)
        {
            var chunk = new JsonObject
            {
                ["id"] = _id, ["object"] = "chat.completion.chunk", ["created"] = _created,
                ["model"] = deployment.ModelName, ["system_fingerprint"] = "fp_foundry_emulator",
                ["choices"] = noChoices ? new JsonArray() : new JsonArray(new JsonObject { ["index"] = 0, ["delta"] = delta, ["finish_reason"] = finish, ["logprobs"] = null }),
            };
            if (usage is not null) chunk["usage"] = usage;
            await response.WriteAsync($"data: {chunk.ToJsonString(FoundryDataPlane.Wire)}\n\n", Encoding.UTF8, ct);
            await response.Body.FlushAsync(ct);
        }

        await Chunk(new JsonObject { ["role"] = "assistant", ["content"] = "" }, null);
        var size = Math.Max(1, plan.ChunkSize);
        var text = Text;
        for (var i = 0; i < text.Length; i += size)
        {
            await Chunk(new JsonObject { ["content"] = text.Substring(i, Math.Min(size, text.Length - i)) }, null);
            if (plan.DelayMs > 0) await Task.Delay(plan.DelayMs, ct);
        }
        var n = 0;
        foreach (var call in Calls)
        {
            await Chunk(new JsonObject
            {
                ["tool_calls"] = new JsonArray(new JsonObject
                {
                    ["index"] = n++, ["id"] = call.Id, ["type"] = "function",
                    ["function"] = new JsonObject { ["name"] = call.Name, ["arguments"] = call.Arguments },
                }),
            }, null);
        }
        await Chunk(new JsonObject(), FinishReason);
        if (includeUsage) await Chunk(new JsonObject(), null, Usage(), noChoices: true);
        await response.WriteAsync("data: [DONE]\n\n", Encoding.UTF8, ct);
        await response.Body.FlushAsync(ct);
    }
}
