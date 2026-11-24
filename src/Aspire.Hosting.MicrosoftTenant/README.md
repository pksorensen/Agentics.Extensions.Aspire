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

## Workload identities: an API, a managed identity, app roles

A service that calls an API with its managed identity in Azure can do the same
locally, with no change to its code:

```csharp
var tenant = builder.AddMicrosoftTenant("home.test", tenantId, o => o.HostPort = 18765)
    .AddApiRegistration("Skills API (local)", skillsApiClientId,
        "api://skills.test/skills-api", ["Organization.ReadWrite", "Marketplace.ReadWrite"]);

var homeIdentity = tenant.AddManagedIdentity("home", homeMiClientId, homeMiObjectId);
tenant.WithAppRoleAssignment(homeIdentity, skillsApiClientId, "Organization.ReadWrite")
      .WithAppRoleAssignment(homeIdentity, skillsApiClientId, "Marketplace.ReadWrite");

builder.AddProject<Projects.Api>("api").WithManagedIdentity(tenant, homeIdentity);
```

`WithManagedIdentity` sets `IDENTITY_ENDPOINT` and `IDENTITY_HEADER`, which is the App
Service and Container Apps managed-identity protocol. It also sets `AZURE_CLIENT_ID`,
`AZURE_TENANT_ID` and `AZURE_TOKEN_CREDENTIALS=ManagedIdentityCredential`. Both
`ManagedIdentityCredential` and `DefaultAzureCredential` (Azure.Identity 1.17) then get
tokens from the emulator's `/msi/token`. The plain-http endpoint is accepted.

The token has the shape Entra issues for an API that requests v2 tokens:

| Claim | Value |
|---|---|
| `iss` | `{public url}/{tenant}/v2.0` |
| `aud` | the API's client ID |
| `oid`, `sub` | the managed identity's object ID |
| `azp`, `appid` | the managed identity's client ID |
| `idtyp` | `app` |
| `tid` | the tenant ID, even over http |
| `roles` | the assigned app roles; left out when there are none |

It is signed with the same key as user tokens, so an API validates it against the
tenant's ordinary JWKS (`/{tenant}/discovery/v2.0/keys`).

`assignmentRequired` (the default) mirrors *Assignment required?* on the enterprise app.
A caller with no role on the API is refused with `AADSTS501051`, instead of being given a
token without roles.

A confidential app registration gets the same JWT through `client_credentials` with
`scope=<app id uri>/.default`. Any other scope, such as Graph, still gets the opaque
token the Graph and ARM emulators expect.

The identity header is generated per AppHost run. The client and object IDs are yours
to choose, so an API can trust the identity by object ID across runs.
