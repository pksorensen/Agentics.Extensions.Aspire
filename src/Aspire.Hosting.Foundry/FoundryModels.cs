namespace Aspire.Hosting.Foundry;

/// <summary>
/// Everything the AppHost tells the emulator, serialised into
/// <c>FOUNDRY_EMULATOR_JSON</c>. Records rather than options so the container and
/// the hosting package agree on one shape, the same split the tenant emulator uses.
/// </summary>
public sealed record FoundrySeed(
    IReadOnlyList<FoundryAccountSeed> Accounts,
    IReadOnlyList<FoundryUsageSeed>? Usage = null,
    IReadOnlyList<FoundryCallerSeed>? Callers = null,
    FoundryBehaviour? Behaviour = null,
    IReadOnlyList<FoundryApiKeySeed>? GatewayKeys = null);

/// <summary>
/// A Foundry (kind <c>AIServices</c>) or Azure OpenAI (kind <c>OpenAI</c>) account,
/// i.e. a <c>Microsoft.CognitiveServices/accounts</c> resource.
/// </summary>
public sealed record FoundryAccountSeed(
    string Name,
    string SubscriptionId,
    string ResourceGroup,
    string Location,
    string Kind,
    IReadOnlyList<FoundryDeploymentSeed> Deployments,
    IReadOnlyList<FoundryApiKeySeed> Keys,
    IReadOnlyDictionary<string, string>? Tags = null,
    IReadOnlyList<string>? Projects = null,
    IReadOnlyList<string>? Aliases = null);

/// <summary>
/// A model deployment. <paramref name="Name"/> is what a client puts in <c>model</c>
/// (v1 routes) or in the <c>/deployments/{name}/</c> path segment.
/// <paramref name="Scenario"/> makes every call to this deployment play one script,
/// which is how a demo deployment can be scripted without touching the client.
/// </summary>
public sealed record FoundryDeploymentSeed(
    string Name,
    string ModelName,
    string ModelVersion,
    string ModelFormat,
    string SkuName,
    int Capacity,
    string? Scenario = null,
    IReadOnlyDictionary<string, string>? Tags = null,
    IReadOnlyList<string>? Aliases = null);

/// <summary>
/// An API key the account accepts. Real accounts have exactly two
/// (<c>key1</c>/<c>key2</c>) and no attribution; the emulator lets a key carry the
/// customer and project it belongs to, because that is the cheapest way to make
/// a demo's usage land on the right customer without a gateway in front.
/// </summary>
public sealed record FoundryApiKeySeed(
    string Name,
    string Value,
    string? Customer = null,
    string? Project = null,
    string? SubscriptionId = null,
    string? UserPrincipal = null,
    string? Initiative = null,
    IReadOnlyDictionary<string, string>? Context = null);

/// <summary>Maps an Entra caller (app id or object id) to a customer/project.</summary>
public sealed record FoundryCallerSeed(
    string CallerId,
    string? Customer,
    string? Project,
    string? DisplayName = null);

/// <summary>
/// Historic usage generated deterministically at startup: one record per day per
/// entry, with tokens jittered around the given daily averages from a fixed seed,
/// so every run of the demo shows the same history.
/// </summary>
public sealed record FoundryUsageSeed(
    string Account,
    string Deployment,
    string? Customer,
    string? Project,
    string? Caller,
    int Days,
    long DailyInputTokens,
    long DailyOutputTokens,
    int DailyRequests,
    int RandomSeed,
    double CachedRatio = 0.0,
    DateTimeOffset? EndDate = null,
    string? SubscriptionId = null,
    string? UserPrincipal = null,
    string? Initiative = null);

public sealed record FoundryBehaviour(
    bool RequireAuth = true,
    bool AllowSimKeys = true,
    string? TokenInfoUrl = null,
    string? ScenarioDirectory = null,
    int DefaultChunkSize = 24,
    int DefaultDelayMs = 0);
