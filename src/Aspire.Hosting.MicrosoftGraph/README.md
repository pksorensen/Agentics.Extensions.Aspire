# Agentics.Extensions.Aspire.MicrosoftGraph

Adds the selected Microsoft Graph surface to a local Microsoft tenant.

```csharp
tenant.WithGraphApi("v1.0");
```

The first provider implements the application, service-principal and password
operations needed by `pks-agent-azure`. Unsupported endpoints return 404 instead
of pretending the complete Graph API is available.
