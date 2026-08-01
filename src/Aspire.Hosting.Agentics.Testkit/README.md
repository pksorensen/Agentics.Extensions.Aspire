# Agentics integration testkit for Aspire

Runs the public Agentics integration testkit as one Aspire container and injects its API,
OAuth, and hosted-Git settings into an integrator application.

```csharp
var agentics = builder.AddAgenticsTestkit("agentics", options =>
{
    options.Owner = "arvoworks";
    options.AppName = "Arvo Works local integration";
});

builder.AddNpmApp("arvo", "../portal")
    .WithAgenticsTestkit(agentics);
```

`WithAgenticsTestkit` supplies `AGENTICS_BASE_URL`, `AGENTICS_TOKEN_URL`,
`AGENTICS_GIT_URL`, `AGENTICS_CLIENT_ID`, `AGENTICS_CLIENT_SECRET`, and
`AGENTICS_SPONSOR_OWNER`, and waits for the testkit health check.

The fixture is ephemeral by default. Set `options.PersistData = true` for a named Docker
volume. Credentials are deterministic and test-only: never expose the resource publicly
or use it for production data.
