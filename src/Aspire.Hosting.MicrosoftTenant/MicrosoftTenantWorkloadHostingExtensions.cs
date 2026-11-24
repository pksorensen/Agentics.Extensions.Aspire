using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.MicrosoftTenant;
using System.Security.Cryptography;

namespace Aspire.Hosting;

/// <summary>
/// Workload identities in the emulated tenant: an API registration that exposes
/// app roles, a managed identity, and the assignment of one to the other — so a
/// service can call an API with <c>ManagedIdentityCredential</c> locally exactly
/// as it does in Azure.
/// </summary>
public static class MicrosoftTenantWorkloadHostingExtensions
{
    /// <summary>
    /// Seeds an API app registration with app roles. Tokens for it are v2:
    /// <c>aud</c> = <paramref name="clientId"/>. With
    /// <paramref name="assignmentRequired"/> (Entra's "Assignment required?") a
    /// caller holding none of the roles is refused a token.
    /// </summary>
    public static IResourceBuilder<MicrosoftTenantResource> AddApiRegistration(
        this IResourceBuilder<MicrosoftTenantResource> tenant,
        string displayName,
        string clientId,
        string appIdUri,
        IReadOnlyList<string> appRoles,
        bool assignmentRequired = true)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(appIdUri);
        ArgumentNullException.ThrowIfNull(appRoles);
        RequireGuid(clientId, nameof(clientId));
        if (tenant.Resource.Apis.Any(a => string.Equals(a.ClientId, clientId, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException($"API '{clientId}' is already seeded.", nameof(clientId));

        tenant.Resource.Apis.Add(new(displayName, clientId, appIdUri.TrimEnd('/'), [.. appRoles], assignmentRequired));
        return tenant;
    }

    /// <summary>
    /// Seeds a managed identity. The ids are yours to choose, so they can stay
    /// constant across runs — an API that trusts the identity by object id needs
    /// that. The identity header (the secret a workload presents) is generated
    /// per AppHost run.
    /// </summary>
    public static MicrosoftTenantManagedIdentity AddManagedIdentity(
        this IResourceBuilder<MicrosoftTenantResource> tenant,
        string name,
        string clientId,
        string objectId)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        RequireGuid(clientId, nameof(clientId));
        RequireGuid(objectId, nameof(objectId));
        if (tenant.Resource.ManagedIdentities.Any(i => string.Equals(i.ClientId, clientId, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException($"Managed identity '{clientId}' is already seeded.", nameof(clientId));

        var identity = new MicrosoftTenantManagedIdentity(
            name, clientId, objectId, Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant());
        tenant.Resource.ManagedIdentities.Add(identity);
        return identity;
    }

    /// <summary>Assigns an app role on an API to a principal — a managed identity
    /// or a confidential app registration, named by its client id.</summary>
    public static IResourceBuilder<MicrosoftTenantResource> WithAppRoleAssignment(
        this IResourceBuilder<MicrosoftTenantResource> tenant,
        string principalClientId,
        string apiClientId,
        string role)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentException.ThrowIfNullOrWhiteSpace(role);
        var api = tenant.Resource.Apis.FirstOrDefault(a => string.Equals(a.ClientId, apiClientId, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"API '{apiClientId}' is not seeded; call AddApiRegistration first.", nameof(apiClientId));
        if (!api.AppRoles.Contains(role, StringComparer.Ordinal))
            throw new ArgumentException($"API '{api.DisplayName}' has no app role '{role}'.", nameof(role));

        tenant.Resource.AppRoleAssignments.Add(new(principalClientId, apiClientId, role));
        return tenant;
    }

    public static IResourceBuilder<MicrosoftTenantResource> WithAppRoleAssignment(
        this IResourceBuilder<MicrosoftTenantResource> tenant,
        MicrosoftTenantManagedIdentity identity,
        string apiClientId,
        string role)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return tenant.WithAppRoleAssignment(identity.ClientId, apiClientId, role);
    }

    /// <summary>
    /// Gives a resource the managed identity, the way App Service and Container
    /// Apps do: <c>IDENTITY_ENDPOINT</c> + <c>IDENTITY_HEADER</c>, which
    /// <c>ManagedIdentityCredential</c> picks up, plus <c>AZURE_CLIENT_ID</c> and
    /// <c>AZURE_TOKEN_CREDENTIALS=ManagedIdentityCredential</c>, which make
    /// <c>DefaultAzureCredential</c> use it instead of a developer login.
    ///
    /// The endpoint is the emulator's address as seen from the resource: the
    /// tenant's public URL when it has one (a host port), otherwise its endpoint.
    /// </summary>
    public static IResourceBuilder<T> WithManagedIdentity<T>(
        this IResourceBuilder<T> resource,
        IResourceBuilder<MicrosoftTenantResource> tenant,
        MicrosoftTenantManagedIdentity identity)
        where T : IResourceWithEnvironment
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(identity);

        var endpoint = tenant.GetEndpoint(MicrosoftTenantResource.HttpEndpointName);
        return resource
            .WithEnvironment(context =>
            {
                context.EnvironmentVariables["IDENTITY_ENDPOINT"] = tenant.Resource.PublicUrl is { } publicUrl
                    ? $"{publicUrl}/msi/token"
                    : ReferenceExpression.Create($"{endpoint.Property(EndpointProperty.Url)}/msi/token");
                context.EnvironmentVariables["IDENTITY_HEADER"] = identity.IdentityHeader;
                context.EnvironmentVariables["AZURE_CLIENT_ID"] = identity.ClientId;
                context.EnvironmentVariables["AZURE_TENANT_ID"] = tenant.Resource.TenantId;
                context.EnvironmentVariables["AZURE_TOKEN_CREDENTIALS"] = "ManagedIdentityCredential";
            });
    }

    private static void RequireGuid(string value, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, name);
        if (!Guid.TryParse(value, out _)) throw new ArgumentException($"'{name}' must be a GUID.", name);
    }
}
