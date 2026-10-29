using System.Text.Json;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Foundry;

namespace Foundry.Emulator.Tests;

public sealed class HostingTests
{
    [Fact]
    public async Task AppHost_seed_round_trips_into_the_emulator_contract()
    {
        var builder = DistributedApplication.CreateBuilder();
        var foundry = builder.AddFoundryEmulator("foundry", o => { o.HostPort = 18499; o.RequireAuth = false; })
            .WithAccount("aif-demo", a =>
            {
                a.Aliases.Add("line-a");
                a.WithDeployment("gpt-5.1", version: "2025-11-13", capacity: 300, aliases: "gpt-5.1-eu").WithKeys("k1");
            })
            .WithGatewayKey("gw-key", "acme", subscriptionId: "acme-user", userPrincipal: "user@example.test")
            .WithSeededUsage("aif-demo", "gpt-5.1", "acme", days: 5);

        Assert.Equal("http://localhost:18499", foundry.Resource.PublicUrl);

        var env = new Dictionary<string, object>();
        var context = new EnvironmentCallbackContext(new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run), env);
        foreach (var callback in foundry.Resource.Annotations.OfType<EnvironmentCallbackAnnotation>())
            await callback.Callback(context);

        var seed = JsonSerializer.Deserialize<FoundrySeed>((string)env["FOUNDRY_EMULATOR_JSON"])!;
        var account = Assert.Single(seed.Accounts);
        Assert.Equal(["line-a"], account.Aliases);
        Assert.Equal("gpt-5.1-eu", Assert.Single(account.Deployments).Aliases![0]);
        Assert.Equal("k1", account.Keys[0].Value);
        Assert.Equal("acme-user", Assert.Single(seed.GatewayKeys!).SubscriptionId);
        Assert.Equal(5, Assert.Single(seed.Usage!).Days);
        Assert.False(seed.Behaviour!.RequireAuth);
        Assert.Equal("http://localhost:18499", env["FOUNDRY_PUBLIC_URL"]);
    }
}
