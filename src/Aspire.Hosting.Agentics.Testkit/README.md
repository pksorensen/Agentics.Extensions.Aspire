# Agentics integration testkit for Aspire

Runs the public Agentics integration testkit as one Aspire container and injects its API,
OAuth, and hosted-Git settings into an integrator application.

The default image is `registry.agentics.dk/agentics/agentics-testkit:latest`. Log in to
`registry.agentics.dk` with a pull-only, image-scoped credential before starting Aspire.
Override it through AppHost configuration with `AGENTICS_TESTKIT_IMAGE` and
`AGENTICS_TESTKIT_TAG`, or set `options.Image` / `options.Tag` in the callback for one resource.

```csharp
var agentics = builder.AddAgenticsTestkit("agentics")
    .WithIntegrationAppRegistration(
        owner: "arvoworks",
        appId: "arvoworks-local",
        appName: "Arvo Works local integration")
    .WithDataVolume("arvoworks-agentics-testkit-data")
    .AddAdminUser(
        email: "admin@arvo.local",
        name: "Arvo Admin",
        handle: "arvo-admin",
        password: "local-development-only");

builder.AddNpmApp("arvo", "../portal")
    .WithAgenticsTestkit(agentics);
```

`WithAgenticsTestkit` supplies `AGENTICS_BASE_URL`, `AGENTICS_TOKEN_URL`,
`AGENTICS_GIT_URL`, `AGENTICS_CLIENT_ID`, `AGENTICS_CLIENT_SECRET`, and
`AGENTICS_SPONSOR_OWNER`, and waits for the testkit health check.
The resource becomes healthy only after Agentics, Keycloak, hosted Git, and the
integration bootstrap are ready, so dependent applications cannot start against a
half-seeded fixture.

The Aspire dashboard shows the three testkit endpoints as **Agentics API**,
**Keycloak**, and **Hosted Git**.

The testkit container maps `host.docker.internal` to the local container host. Integrator
webhook URLs can therefore call a host-process resource with, for example,
`http://host.docker.internal:{port}/api/webhooks` on both Linux and Docker Desktop.

`AddAdminUser` creates a deterministic interactive Keycloak identity, its Agentics
profile/personal handle, and grants the `global-admin` realm role. This is login and
platform administration only; it deliberately does not bypass private-project membership.

The fixture is ephemeral by default. Call `WithDataVolume()` for a persistent named Docker
volume. Credentials are deterministic and test-only: never expose the resource publicly
or use it for production data.
