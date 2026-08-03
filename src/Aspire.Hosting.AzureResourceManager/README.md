# Agentics.Extensions.Aspire.AzureResourceManager

An intentionally partial, API-version-aware ARM emulator. It routes a request to
the provider matching the namespace after `/providers/`; a provider may handle
one resource type or a complete namespace.

```csharp
var rg = tenant.WithArmApi()
    .AddResourceGroup(subscriptionId, "rg-comms");

rg.AddCustomRole("ACS Identity Provisioner", roleId, actions)
  .AddCommunicationService("acs-arvo", "email-arvo", "mail.arvo.works");
```

Third-party emulator hosts can add a partial provider without changing the ARM
router:

```csharp
services.RegisterArmProviderEmulator<MyWidgetsProvider>("Contoso.Widgets");
```

`IArmProviderEmulator` receives the provider namespace, resource path, HTTP
method, body and `api-version`. Returning `null` lets another handler for the
same namespace try the request. This makes small providers composable and keeps
OpenAPI-generated handlers possible later.
