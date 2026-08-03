using System.Text.Json;

namespace Aspire.Hosting.AzureResourceManager.Emulator;

public sealed class MicrosoftAuthorizationProviderEmulator(AzureResourceManagerState state)
    : IArmProviderEmulator
{
    private static readonly HashSet<string> Versions = ["2022-04-01", "2022-05-01-preview"];

    public ValueTask<ArmProviderResponse?> HandleAsync(
        ArmProviderRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!Versions.Contains(request.ApiVersion ?? ""))
            return ValueTask.FromResult<ArmProviderResponse?>(
                ArmProviderResponse.InvalidApiVersion(request.ProviderNamespace, request.ApiVersion));

        if (IsType(request.ResourcePath, "roleAssignments"))
            return ValueTask.FromResult<ArmProviderResponse?>(Mutable(request));

        if (request.Method == "GET" &&
            IsType(request.ResourcePath, "roleDefinitions"))
        {
            var id = request.ResourcePath.Split('/').Last();
            var role = state.Seed.Subscriptions.SelectMany(item => item.CustomRoles)
                .FirstOrDefault(item => item.RoleDefinitionId == id);
            return ValueTask.FromResult<ArmProviderResponse?>(role is null
                ? ArmProviderResponse.NotFound($"Role definition '{id}' was not seeded.")
                : ArmProviderResponse.Ok(new
                {
                    id = request.ResourceId,
                    name = role.RoleDefinitionId,
                    properties = new
                    {
                        roleName = role.Name,
                        description = role.Description,
                        permissions = new[]
                        {
                            new
                            {
                                actions = role.Actions,
                                notActions = role.NotActions,
                                dataActions = role.DataActions,
                                notDataActions = role.NotDataActions,
                            },
                        },
                        assignableScopes = role.AssignableScopes,
                    },
                }));
        }

        return ValueTask.FromResult<ArmProviderResponse?>(null);
    }

    private static bool IsType(string path, string type) =>
        path.StartsWith(type + "/", StringComparison.OrdinalIgnoreCase) ||
        path.Contains("/" + type + "/", StringComparison.OrdinalIgnoreCase);

    private ArmProviderResponse Mutable(ArmProviderRequest request)
    {
        if (request.Method == "PUT" && request.Body is { } body)
        {
            state.Resources[request.ResourceId] = body;
            state.Save();
            return ArmProviderResponse.Ok(new { id = request.ResourceId, name = request.ResourcePath.Split('/').Last() });
        }
        if (request.Method == "DELETE")
        {
            if (!state.Resources.TryRemove(request.ResourceId, out _))
                return ArmProviderResponse.NotFound($"'{request.ResourceId}' was already gone.");
            state.Save();
            return ArmProviderResponse.Ok(new { name = request.ResourcePath.Split('/').Last() });
        }
        return ArmProviderResponse.NotFound($"Method {request.Method} is not implemented.");
    }
}

public sealed class MicrosoftCommunicationProviderEmulator(AzureResourceManagerState state)
    : IArmProviderEmulator
{
    private static readonly Dictionary<string, HashSet<string>> Versions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["communicationServices"] = ["2023-04-01"],
        ["smtpUsernames"] = ["2024-09-01-preview", "2025-01-25-preview", "2025-09-01", "2025-09-01-preview", "2026-03-18"],
        ["senderUsernames"] = ["2023-04-01"],
    };

    public ValueTask<ArmProviderResponse?> HandleAsync(
        ArmProviderRequest request,
        CancellationToken cancellationToken = default)
    {
        var kind = request.ResourcePath.Contains("/senderUsernames/", StringComparison.OrdinalIgnoreCase)
            ? "senderUsernames"
            : request.ResourcePath.Contains("/smtpUsernames/", StringComparison.OrdinalIgnoreCase)
                ? "smtpUsernames"
                : request.ResourcePath.StartsWith("communicationServices/", StringComparison.OrdinalIgnoreCase)
                    ? "communicationServices"
                    : null;
        if (kind is null) return ValueTask.FromResult<ArmProviderResponse?>(null);
        if (!Versions[kind].Contains(request.ApiVersion ?? ""))
            return ValueTask.FromResult<ArmProviderResponse?>(
                ArmProviderResponse.InvalidApiVersion(request.ProviderNamespace, request.ApiVersion));

        if (kind == "communicationServices" && request.Method == "GET")
            return ValueTask.FromResult<ArmProviderResponse?>(ReadCommunicationService(request));

        if (request.Method == "PUT" && request.Body is { } body)
        {
            state.Resources[request.ResourceId] = body;
            state.Save();
            return ValueTask.FromResult<ArmProviderResponse?>(ArmProviderResponse.Ok(new
            {
                id = request.ResourceId,
                name = request.ResourcePath.Split('/').Last(),
            }));
        }
        if (request.Method == "DELETE")
        {
            if (!state.Resources.TryRemove(request.ResourceId, out _))
                return ValueTask.FromResult<ArmProviderResponse?>(
                    ArmProviderResponse.NotFound($"'{request.ResourceId}' was already gone."));
            state.Save();
            return ValueTask.FromResult<ArmProviderResponse?>(
                ArmProviderResponse.Ok(new { name = request.ResourcePath.Split('/').Last() }));
        }

        return ValueTask.FromResult<ArmProviderResponse?>(
            ArmProviderResponse.NotFound($"Method {request.Method} is not implemented."));
    }

    private ArmProviderResponse ReadCommunicationService(ArmProviderRequest request)
    {
        var parts = request.ResourceId.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var subscriptionId = SegmentAfter(parts, "subscriptions");
        var resourceGroup = SegmentAfter(parts, "resourceGroups");
        var name = request.ResourcePath.Split('/').ElementAtOrDefault(1);
        var service = state.Seed.Subscriptions
            .FirstOrDefault(item => item.SubscriptionId == subscriptionId)?.ResourceGroups
            .FirstOrDefault(item => item.Name == resourceGroup)?.CommunicationServices
            .FirstOrDefault(item => item.Name == name);
        if (service is null)
            return ArmProviderResponse.NotFound($"Communication service '{name}' was not seeded.");

        var domains = service.LinkedDomains.Select(domain =>
            $"/subscriptions/{subscriptionId}/resourceGroups/{resourceGroup}" +
            $"/providers/Microsoft.Communication/emailServices/{domain.EmailServiceName}/domains/{domain.Domain}");
        return ArmProviderResponse.Ok(new
        {
            id = request.ResourceId,
            name = service.Name,
            properties = new { dataLocation = service.DataLocation, linkedDomains = domains },
        });
    }

    private static string? SegmentAfter(string[] segments, string marker)
    {
        var index = Array.FindIndex(segments, item => item.Equals(marker, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < segments.Length ? segments[index + 1] : null;
    }
}
