using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Foundry;

/// <summary>
/// A local stand-in for Azure AI Foundry (and Azure OpenAI): the OpenAI-compatible
/// inference routes, a <c>Microsoft.CognitiveServices</c> + <c>Microsoft.Insights</c>
/// ARM surface, scripted LLM playback and a usage log — one container.
/// </summary>
public sealed class FoundryResource : ContainerResource
{
    internal FoundryResource(string name) : base(name) { }

    public const string HttpEndpointName = "http";

    /// <summary>The address clients reach the emulator on, when it is knowable before
    /// startup (a pinned <see cref="FoundryOptions.HostPort"/> or an explicit
    /// <see cref="FoundryOptions.PublicUrl"/>). Only used in the endpoints the ARM
    /// surface reports; inference works without it.</summary>
    public string? PublicUrl { get; init; }

    public IList<FoundryAccountSeed> Accounts { get; } = [];
    public IList<FoundryUsageSeed> Usage { get; } = [];
    public IList<FoundryCallerSeed> Callers { get; } = [];
    public IList<FoundryApiKeySeed> GatewayKeys { get; } = [];

    internal FoundryBehaviour Behaviour { get; set; } = new();

    /// <summary>The OpenAI v1 base URL of one account, e.g. for an app's
    /// <c>OPENAI_BASE_URL</c> or Codex's <c>base_url</c>.</summary>
    public ReferenceExpression OpenAIBaseUrl(string accountOrAlias) =>
        ReferenceExpression.Create($"{GetEndpoint(HttpEndpointName)}/{accountOrAlias}/openai/v1");

    /// <summary>The ARM base this emulator answers on — <c>{endpoint}/arm</c>, the
    /// stand-in for <c>https://management.azure.com</c>.</summary>
    public ReferenceExpression ArmBaseUrl => ReferenceExpression.Create($"{GetEndpoint(HttpEndpointName)}/arm");

    private EndpointReference GetEndpoint(string name) => new(this, name);
}
