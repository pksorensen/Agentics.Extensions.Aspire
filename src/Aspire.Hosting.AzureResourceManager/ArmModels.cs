using System.Text.Json;

namespace Aspire.Hosting.AzureResourceManager;

public sealed record AzureResourceManagerSeed(
    bool Enabled,
    IReadOnlyList<ArmSubscriptionSeed> Subscriptions);

public sealed record ArmSubscriptionSeed(
    string SubscriptionId,
    IReadOnlyList<ArmResourceGroupSeed> ResourceGroups,
    IReadOnlyList<ArmCustomRoleSeed> CustomRoles);

public sealed record ArmResourceGroupSeed(
    string Name,
    string Location,
    IReadOnlyList<ArmCommunicationServiceSeed> CommunicationServices);

public sealed record ArmCustomRoleSeed(
    string Name,
    string RoleDefinitionId,
    string? Description,
    IReadOnlyList<string> Actions,
    IReadOnlyList<string> NotActions,
    IReadOnlyList<string> DataActions,
    IReadOnlyList<string> NotDataActions,
    IReadOnlyList<string> AssignableScopes);

public sealed record ArmCommunicationServiceSeed(
    string Name,
    string DataLocation,
    IReadOnlyList<ArmEmailDomainSeed> LinkedDomains);

public sealed record ArmEmailDomainSeed(string EmailServiceName, string Domain);

public sealed record ArmProviderRequest(
    string Method,
    string ResourceId,
    string ProviderNamespace,
    string ResourcePath,
    string? ApiVersion,
    JsonElement? Body);

public sealed record ArmProviderResponse(int StatusCode, object? Body = null)
{
    public static ArmProviderResponse Ok(object body) => new(200, body);
    public static ArmProviderResponse Created(object body) => new(201, body);
    public static ArmProviderResponse NoContent() => new(204);
    public static ArmProviderResponse NotFound(string message) =>
        new(404, new { error = new { code = "ResourceNotFound", message } });
    public static ArmProviderResponse InvalidApiVersion(string provider, string? version) =>
        new(400, new { error = new { code = "NoRegisteredProviderFound", message = $"{provider} does not support api-version '{version}'." } });
}

public interface IArmProviderEmulator
{
    ValueTask<ArmProviderResponse?> HandleAsync(
        ArmProviderRequest request,
        CancellationToken cancellationToken = default);
}
