using Microsoft.AspNetCore.Mvc.Testing;

namespace Foundry.Emulator.Tests;

/// <summary>One emulator for the whole run. Configuration goes in through environment
/// variables because Program reads it while the builder is being created.</summary>
public sealed class EmulatorFixture : IDisposable
{
    public const string AccountKey = "test-account-key";
    public const string GatewayKey = "test-gateway-key";

    private readonly WebApplicationFactory<Program> _factory;

    public EmulatorFixture()
    {
        var seed = $$"""
        {"Accounts":[{"Name":"aif-test","SubscriptionId":"00000000-0000-4000-8000-0000000000f1","ResourceGroup":"rg-test","Location":"swedencentral","Kind":"AIServices",
          "Deployments":[{"Name":"gpt-test","ModelName":"gpt-5-codex","ModelVersion":"2025-09-15","ModelFormat":"OpenAI","SkuName":"GlobalStandard","Capacity":50,"Aliases":["gpt-test-eu"]}],
          "Keys":[{"Name":"key1","Value":"{{AccountKey}}"}],"Aliases":["line-a"]}],
         "GatewayKeys":[{"Name":"acme-user","Value":"{{GatewayKey}}","Customer":"acme","SubscriptionId":"acme-user","UserPrincipal":"user@example.test"}],
         "Usage":[{"Account":"aif-test","Deployment":"gpt-test","Customer":"seeded-co","Project":null,"Caller":"seeded-co","Days":7,"DailyInputTokens":1000,"DailyOutputTokens":100,"DailyRequests":10,"RandomSeed":1}]}
        """;
        Environment.SetEnvironmentVariable("FOUNDRY_EMULATOR_JSON", seed);
        Environment.SetEnvironmentVariable("FOUNDRY_SCENARIOS_DIR", Path.Combine(AppContext.BaseDirectory, "scenarios"));
        _factory = new WebApplicationFactory<Program>();
        Client = _factory.CreateClient();
    }

    public HttpClient Client { get; }

    public void Dispose() => _factory.Dispose();
}

[CollectionDefinition(nameof(EmulatorCollection))]
public sealed class EmulatorCollection : ICollectionFixture<EmulatorFixture>;
