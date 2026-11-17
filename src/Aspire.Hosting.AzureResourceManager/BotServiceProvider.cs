using System.Text.Json;
using System.Text.Json.Nodes;

namespace Aspire.Hosting.AzureResourceManager.Emulator;

/// <summary>
/// A minimal <c>Microsoft.BotService</c>: PUT/GET/DELETE <c>botServices/{name}</c> and PUT/GET
/// <c>botServices/{name}/channels/{channel}</c>, stored in <see cref="AzureResourceManagerState.Resources"/>
/// so the tenant portal lists them with everything else. It keeps the four ARM behaviours a caller
/// is most likely to get wrong locally and only find out about in a real subscription:
/// the resource group must exist, a bot name is unique across the whole cloud (not per group),
/// a channel needs its bot, and deleting a bot deletes its channels.
/// </summary>
public sealed class MicrosoftBotServiceProviderEmulator(AzureResourceManagerState state)
    : IArmProviderEmulator
{
    private static readonly HashSet<string> Versions = ["2022-09-15", "2023-09-15-preview"];

    public ValueTask<ArmProviderResponse?> HandleAsync(
        ArmProviderRequest request,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Handle(request));

    private ArmProviderResponse? Handle(ArmProviderRequest request)
    {
        var path = request.ResourcePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (path.Length < 2 || !path[0].Equals("botServices", StringComparison.OrdinalIgnoreCase)) return null;

        var isChannel = path.Length == 4 && path[2].Equals("channels", StringComparison.OrdinalIgnoreCase);
        if (path.Length != 2 && !isChannel) return null;

        if (!Versions.Contains(request.ApiVersion ?? ""))
            return ArmProviderResponse.InvalidApiVersion(request.ProviderNamespace, request.ApiVersion);

        var (subscriptionId, resourceGroup) = Scope(request.ResourceId);
        if (!ResourceGroupExists(subscriptionId, resourceGroup))
            return new(404, new
            {
                error = new { code = "ResourceGroupNotFound", message = $"Resource group '{resourceGroup}' could not be found." },
            });

        var botName = path[1];
        var botId = request.ResourceId[..(request.ResourceId.IndexOf("/providers/", StringComparison.OrdinalIgnoreCase)
                                          + "/providers/Microsoft.BotService/botServices/".Length)] + botName;

        return (request.Method, isChannel) switch
        {
            ("GET", _) => state.Resources.TryGetValue(request.ResourceId, out var stored)
                ? ArmProviderResponse.Ok(stored)
                : ArmProviderResponse.NotFound($"'{request.ResourceId}' was not found."),
            ("PUT", false) => PutBot(request, botName),
            ("PUT", true) => PutChannel(request, botId, path[3]),
            ("DELETE", false) => DeleteBot(request.ResourceId),
            ("DELETE", true) => state.Resources.TryRemove(request.ResourceId, out _)
                ? Saved(ArmProviderResponse.Ok(new { name = path[3] }))
                : ArmProviderResponse.NoContent(),
            _ => ArmProviderResponse.NotFound($"Method {request.Method} is not implemented."),
        };
    }

    private ArmProviderResponse PutBot(ArmProviderRequest request, string name)
    {
        if (request.Body is not { } body || JsonNode.Parse(body.GetRawText()) is not JsonObject node)
            return BadRequest("A request body is required.");

        var properties = node["properties"] as JsonObject;
        var missing = new[] { "displayName", "endpoint", "msaAppId" }
            .Where(field => string.IsNullOrWhiteSpace(properties?[field]?.GetValue<string>()))
            .ToList();
        if (missing.Count > 0) return BadRequest($"properties.{string.Join(", properties.", missing)} required.");

        if (!string.Equals(node["location"]?.GetValue<string>(), "global", StringComparison.OrdinalIgnoreCase))
            return BadRequest("An Azure Bot must be created in location 'global'.");

        // Bot names are global: the same name in another resource group or subscription is taken.
        var clash = state.Resources.Keys.FirstOrDefault(id =>
            id.EndsWith($"/providers/Microsoft.BotService/botServices/{name}", StringComparison.OrdinalIgnoreCase) &&
            !id.Equals(request.ResourceId, StringComparison.OrdinalIgnoreCase));
        if (clash is not null)
            return new(409, new
            {
                error = new { code = "Conflict", message = $"Bot name '{name}' is already in use. Bot names are globally unique." },
            });

        var existed = state.Resources.ContainsKey(request.ResourceId);
        node["id"] = request.ResourceId;
        node["name"] = name;
        node["type"] = "Microsoft.BotService/botServices";
        properties!["provisioningState"] = "Succeeded";
        properties["msaAppType"] ??= "MultiTenant";

        var stored = JsonSerializer.SerializeToElement(node);
        state.Resources[request.ResourceId] = stored;
        return Saved(existed ? ArmProviderResponse.Ok(stored) : ArmProviderResponse.Created(stored));
    }

    private ArmProviderResponse PutChannel(ArmProviderRequest request, string botId, string channel)
    {
        if (!state.Resources.ContainsKey(botId))
            return ArmProviderResponse.NotFound($"Bot '{botId}' was not found.");
        if (request.Body is not { } body || JsonNode.Parse(body.GetRawText()) is not JsonObject node)
            return BadRequest("A request body is required.");

        var existed = state.Resources.ContainsKey(request.ResourceId);
        node["id"] = request.ResourceId;
        node["name"] = channel;
        node["type"] = "Microsoft.BotService/botServices/channels";
        if (node["properties"] is JsonObject properties) properties["provisioningState"] = "Succeeded";

        var stored = JsonSerializer.SerializeToElement(node);
        state.Resources[request.ResourceId] = stored;
        return Saved(existed ? ArmProviderResponse.Ok(stored) : ArmProviderResponse.Created(stored));
    }

    private ArmProviderResponse DeleteBot(string botId)
    {
        if (!state.Resources.TryRemove(botId, out _)) return ArmProviderResponse.NoContent();

        foreach (var channel in state.Resources.Keys
                     .Where(id => id.StartsWith(botId + "/channels/", StringComparison.OrdinalIgnoreCase))
                     .ToList())
        {
            state.Resources.TryRemove(channel, out _);
        }

        return Saved(ArmProviderResponse.Ok(new { id = botId }));
    }

    private ArmProviderResponse Saved(ArmProviderResponse response)
    {
        state.Save();
        return response;
    }

    private bool ResourceGroupExists(string? subscriptionId, string? resourceGroup) =>
        state.Seed.Subscriptions
            .FirstOrDefault(item => string.Equals(item.SubscriptionId, subscriptionId, StringComparison.OrdinalIgnoreCase))?
            .ResourceGroups.Any(item => string.Equals(item.Name, resourceGroup, StringComparison.OrdinalIgnoreCase)) == true;

    private static (string? SubscriptionId, string? ResourceGroup) Scope(string resourceId)
    {
        var parts = resourceId.Split('/', StringSplitOptions.RemoveEmptyEntries);
        string? After(string marker)
        {
            var index = Array.FindIndex(parts, item => item.Equals(marker, StringComparison.OrdinalIgnoreCase));
            return index >= 0 && index + 1 < parts.Length ? parts[index + 1] : null;
        }

        return (After("subscriptions"), After("resourceGroups"));
    }

    private static ArmProviderResponse BadRequest(string message) =>
        new(400, new { error = new { code = "InvalidRequest", message } });
}
