# Agentics.Extensions.Aspire.MicrosoftTenant

Models a local Microsoft tenant as an Aspire resource and supplies the OAuth
client-credentials endpoint shared by the Graph and ARM emulator packages.

```csharp
var tenant = builder.AddMicrosoftTenant(
    "kjeldager.com",
    "00000000-0000-0000-0000-000000000001")
    .AddAppRegistration(
        "azure-resource-api",
        "00000000-0000-0000-0000-000000000002",
        "local-only-secret");
```

This is a development emulator. It is not an Entra security test product.

Opening the tenant resource endpoint shows a built-in read-only portal for app registrations,
service principals, subscriptions, resource groups, custom roles, Communication Services and
dynamically provisioned ARM objects. It exposes metadata only; seeded client secrets and generated
credentials are never part of the portal state.

The portal understands Azure Portal-compatible hash routes such as
`#view/Microsoft_AAD_RegisteredApps/ApplicationMenuBlade/~/Overview/appId/{appId}`
and `#resource/{resourceId}/overview`. A consuming app can therefore use the
tenant endpoint as its local portal base URL without changing deep-link shapes.

Set `PersistData = true` to retain dynamically provisioned Graph applications,
service principals and ARM objects in the tenant resource's data volume across
AppHost sessions. Generated credential values are never persisted.
