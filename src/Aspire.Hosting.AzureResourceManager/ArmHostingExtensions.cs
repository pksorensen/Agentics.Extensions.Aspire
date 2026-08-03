using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.AzureResourceManager;
using Aspire.Hosting.MicrosoftTenant;

namespace Aspire.Hosting;

public static class AzureResourceManagerHostingExtensions
{
    private const string FeatureKey = "AZURE_RESOURCE_MANAGER_EMULATOR_JSON";

    public static IResourceBuilder<MicrosoftTenantResource> WithArmApi(
        this IResourceBuilder<MicrosoftTenantResource> tenant) =>
        tenant.WithAzureResourceManagerApi();

    public static IResourceBuilder<MicrosoftTenantResource> WithAzureResourceManagerApi(
        this IResourceBuilder<MicrosoftTenantResource> tenant)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        Seed(tenant).Enabled = true;
        return tenant;
    }

    public static ArmResourceGroupBuilder AddResourceGroup(
        this IResourceBuilder<MicrosoftTenantResource> tenant,
        string subscriptionId,
        string name,
        string location = "westeurope")
    {
        ArgumentNullException.ThrowIfNull(tenant);
        if (!Guid.TryParse(subscriptionId, out _))
            throw new ArgumentException("A subscription id must be a GUID.", nameof(subscriptionId));
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var seed = Seed(tenant);
        seed.Enabled = true;
        var subscription = seed.Subscriptions.FirstOrDefault(item => item.SubscriptionId == subscriptionId);
        if (subscription is null)
        {
            subscription = new MutableSubscription(subscriptionId);
            seed.Subscriptions.Add(subscription);
        }

        if (subscription.ResourceGroups.Any(group => group.Name == name))
            throw new ArgumentException($"Resource group '{name}' is already seeded.", nameof(name));

        var resourceGroup = new MutableResourceGroup(name, location);
        subscription.ResourceGroups.Add(resourceGroup);
        return new ArmResourceGroupBuilder(tenant, subscription, resourceGroup);
    }

    private static MutableArmSeed Seed(IResourceBuilder<MicrosoftTenantResource> tenant)
    {
        if (tenant.Resource.Features.TryGetValue(FeatureKey, out var value) && value is MutableArmSeed seed)
            return seed;

        seed = new MutableArmSeed();
        tenant.Resource.Features[FeatureKey] = seed;
        return seed;
    }
}

public sealed class ArmResourceGroupBuilder
{
    internal ArmResourceGroupBuilder(
        IResourceBuilder<MicrosoftTenantResource> tenant,
        MutableSubscription subscription,
        MutableResourceGroup resourceGroup)
    {
        Tenant = tenant;
        Subscription = subscription;
        ResourceGroup = resourceGroup;
    }

    public IResourceBuilder<MicrosoftTenantResource> Tenant { get; }
    internal MutableSubscription Subscription { get; }
    internal MutableResourceGroup ResourceGroup { get; }

    public ArmResourceGroupBuilder AddCustomRole(
        string name,
        string roleDefinitionId,
        IEnumerable<string> actions,
        IEnumerable<string>? dataActions = null,
        IEnumerable<string>? notActions = null,
        IEnumerable<string>? notDataActions = null,
        string? description = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!Guid.TryParse(roleDefinitionId, out _))
            throw new ArgumentException("A role definition id must be a GUID.", nameof(roleDefinitionId));

        Subscription.CustomRoles.Add(new(
            name,
            roleDefinitionId,
            description,
            actions.ToList(),
            notActions?.ToList() ?? [],
            dataActions?.ToList() ?? [],
            notDataActions?.ToList() ?? [],
            [$"/subscriptions/{Subscription.SubscriptionId}/resourceGroups/{ResourceGroup.Name}"]));
        return this;
    }

    public ArmResourceGroupBuilder AddCommunicationService(
        string name,
        string emailServiceName,
        string linkedDomain,
        string dataLocation = "Europe")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(emailServiceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(linkedDomain);

        ResourceGroup.CommunicationServices.Add(new(
            name,
            dataLocation,
            [new(emailServiceName, linkedDomain)]));
        return this;
    }
}

public sealed class MutableArmSeed
{
    public bool Enabled { get; set; }
    public List<MutableSubscription> Subscriptions { get; } = [];
}

public sealed class MutableSubscription(string subscriptionId)
{
    public string SubscriptionId { get; } = subscriptionId;
    public List<MutableResourceGroup> ResourceGroups { get; } = [];
    public List<ArmCustomRoleSeed> CustomRoles { get; } = [];
}

public sealed class MutableResourceGroup(string name, string location)
{
    public string Name { get; } = name;
    public string Location { get; } = location;
    public List<ArmCommunicationServiceSeed> CommunicationServices { get; } = [];
}
