using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Foundry.Emulator;

/// <summary>
/// Home's MCP server, as a stand-in. <c>choose_project</c> shows the caller's projects as an MCP app (a
/// <c>ui://</c> page the agent renders in the chat), and picking one puts its name in the conversation.
/// <c>show_pipeline</c> shows where the newest push is, see <see cref="FoundryMcpPipeline"/>. Streamable HTTP without sessions, authenticated with the same key as the model
/// calls, so the projects are the caller's own. A host that does not render apps gets the tool result's
/// text, and the playback asks the question again in the chat.
/// </summary>
internal static class FoundryMcp
{
    /// <summary>The name the agent's config gives the server. Codex lists it in tool_search's description.</summary>
    public const string ServerName = "home";

    /// <summary>The namespace Codex puts the server's tools in.</summary>
    public const string Namespace = "mcp__" + ServerName;

    public const string PickerTool = "choose_project";

    private const string PickerUri = "ui://home/choose-project.html";
    private const string AppMime = "text/html;profile=mcp-app";

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        foreach (var path in new[] { "/mcp", "/{account}/mcp" })
        {
            endpoints.MapPost(path, Handle);
            // No server-to-client stream: a GET says so, and the client carries on with POSTs.
            endpoints.MapGet(path, () => Results.StatusCode(StatusCodes.Status405MethodNotAllowed));
        }
    }

    /// <summary>Whether a tool_search output holds the picker.</summary>
    public static bool FoundPicker(JsonNode? tools) =>
        tools is JsonArray list && list.OfType<JsonObject>().Any(t =>
            t["name"]?.GetValue<string>() == Namespace &&
            t["tools"] is JsonArray inner && inner.OfType<JsonObject>().Any(f => f["name"]?.GetValue<string>() == PickerTool));

    private static async Task<IResult> Handle(HttpContext context, FoundryState state, FoundryAuthenticator auth, IConfiguration configuration, IHttpClientFactory httpClients, ILoggerFactory loggers)
    {
        JsonObject? message;
        try
        {
            message = await JsonNode.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted) as JsonObject;
        }
        catch (JsonException)
        {
            message = null;
        }
        if (message is null) return Error(null, -32700, "Parse error");

        var id = message["id"]?.DeepClone();
        var method = message["method"]?.GetValue<string>() ?? "";
        loggers.CreateLogger("Foundry.Emulator.Mcp").LogInformation("MCP {Method} {Name}",
            method, message["params"]?["name"]?.GetValue<string>() ?? message["params"]?["uri"]?.GetValue<string>() ?? "");
        // A notification gets no answer.
        if (id is null) return Results.Accepted();

        var name = context.Request.RouteValues["account"] as string;
        if ((name is null ? state.DefaultAccountFor(null) : state.FindAccount(name)) is not { } account)
            return Error(id, -32001, "No Foundry account by that name.");
        var (caller, denied) = await auth.AuthenticateAsync(context.Request, account, context.RequestAborted);
        if (caller is null) return Results.Json(new { error = new { code = "401", message = denied } }, statusCode: 401);

        if (method == "tools/call" && message["params"]?["name"]?.GetValue<string>() == FoundryMcpPipeline.Tool)
        {
            var wanted = message["params"]?["arguments"]?["project"]?.GetValue<string>();
            return Result(id, await FoundryMcpPipeline.ResultAsync(
                FoundryProjectChoice.For(state, caller), wanted, configuration["FOUNDRY_DATA_DIR"], httpClients.CreateClient(FoundryMcpPipeline.HomeClient), AppMeta(FoundryMcpPipeline.Uri), context.RequestAborted));
        }

        return method switch
        {
            "initialize" => Result(id, new JsonObject
            {
                ["protocolVersion"] = message["params"]?["protocolVersion"]?.DeepClone() ?? "2025-06-18",
                ["capabilities"] = new JsonObject
                {
                    ["tools"] = new JsonObject(),
                    ["resources"] = new JsonObject(),
                    ["extensions"] = new JsonObject { ["io.modelcontextprotocol/ui"] = new JsonObject() },
                },
                ["serverInfo"] = new JsonObject { ["name"] = ServerName, ["title"] = "Home", ["version"] = "1.0.0" },
            }),
            "ping" => Result(id, new JsonObject()),
            "tools/list" => Result(id, new JsonObject { ["tools"] = new JsonArray(PickerToolDefinition(), FoundryMcpPipeline.Definition(AppMeta(FoundryMcpPipeline.Uri))) }),
            "tools/call" when message["params"]?["name"]?.GetValue<string>() == PickerTool =>
                Result(id, PickerResult(FoundryProjectChoice.For(state, caller))),
            "tools/call" => Error(id, -32602, "Unknown tool."),
            "resources/list" => Result(id, new JsonObject
            {
                ["resources"] = new JsonArray(
                    new JsonObject { ["uri"] = PickerUri, ["name"] = "Choose project", ["mimeType"] = AppMime },
                    new JsonObject { ["uri"] = FoundryMcpPipeline.Uri, ["name"] = "Pipeline", ["mimeType"] = AppMime }),
            }),
            "resources/templates/list" => Result(id, new JsonObject { ["resourceTemplates"] = new JsonArray() }),
            "resources/read" when message["params"]?["uri"]?.GetValue<string>() == PickerUri => Result(id, App(PickerUri, FoundryMcpPicker.Html)),
            "resources/read" when message["params"]?["uri"]?.GetValue<string>() == FoundryMcpPipeline.Uri => Result(id, App(FoundryMcpPipeline.Uri, FoundryMcpPipeline.Html)),
            "resources/read" => Error(id, -32002, "Resource not found."),
            _ => Error(id, -32601, "Method not found."),
        };
    }

    private static JsonObject PickerToolDefinition() => new()
    {
        ["name"] = PickerTool,
        ["title"] = "Choose project",
        ["description"] = "Show the person's projects in Home so they can pick the one this work belongs to. The pick comes back as their next message.",
        ["inputSchema"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() },
        // Read-only, so Codex runs it without asking for approval first.
        ["annotations"] = new JsonObject { ["readOnlyHint"] = true, ["destructiveHint"] = false, ["openWorldHint"] = false },
        ["_meta"] = AppMeta(PickerUri),
    };

    private static JsonObject App(string uri, string html) => new()
    {
        ["contents"] = new JsonArray(new JsonObject
        {
            ["uri"] = uri,
            ["mimeType"] = AppMime,
            ["text"] = html,
            ["_meta"] = new JsonObject { ["ui"] = new JsonObject { ["prefersBorder"] = false } },
        }),
    };

    // The MCP Apps key, plus the Apps SDK keys ChatGPT-family hosts read.
    private static JsonObject AppMeta(string uri) => new()
    {
        ["ui"] = new JsonObject { ["resourceUri"] = uri },
        ["openai/outputTemplate"] = uri,
        ["openai/widgetAccessible"] = true,
        ["openai/resultCanProduceWidget"] = true,
    };

    private static JsonObject PickerResult(IReadOnlyList<FoundryProjectChoice> projects)
    {
        var list = new JsonArray();
        for (var i = 0; i < projects.Count; i++)
        {
            var parts = projects[i].Label.Split(" / ", 2);
            list.Add(new JsonObject
            {
                ["number"] = i + 1,
                ["label"] = projects[i].Label,
                ["solution"] = parts.Length == 2 ? parts[0] : null,
                ["project"] = parts.Length == 2 ? parts[1] : parts[0],
                ["hasRepository"] = projects[i].GitRemote is { Length: > 0 },
            });
        }
        var data = new JsonObject { ["projects"] = list };
        var meta = AppMeta(PickerUri);
        meta["home/projects"] = list.DeepClone();
        return new JsonObject
        {
            ["content"] = new JsonArray(new JsonObject
            {
                ["type"] = "text",
                ["text"] = projects.Count == 0
                    ? "The person has no projects in Home yet."
                    : $"Showing {projects.Count} project(s) in the picker. Wait for the person to pick one.",
            }),
            ["structuredContent"] = data,
            ["_meta"] = meta,
        };
    }

    private static IResult Result(JsonNode id, JsonObject result) =>
        Results.Content(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result }.ToJsonString(FoundryDataPlane.Wire), "application/json");

    private static IResult Error(JsonNode? id, int code, string text) =>
        Results.Content(new JsonObject
        {
            ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = text },
        }.ToJsonString(FoundryDataPlane.Wire), "application/json");
}
