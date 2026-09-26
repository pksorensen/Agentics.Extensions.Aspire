using System.Text.Json.Nodes;

namespace Aspire.Hosting.UserData;

/// <summary>
/// Describes where a resource's production data lives, and what must be stripped out of it
/// before it is allowed onto a developer machine.
/// </summary>
public sealed class UserDataSyncOptions
{
    /// <summary>SSH destination of the box production runs on, e.g. <c>root@10.0.0.1</c>.</summary>
    public string? Host { get; set; }

    /// <summary>
    /// Absolute path to the data directory on <see cref="Host"/>. Left null when
    /// <see cref="CoolifyFqdn"/> is set, because it is then discovered at fetch time.
    /// </summary>
    public string? RemotePath { get; set; }

    /// <summary>
    /// Public hostname of the Coolify application owning the data. The remote path is derived
    /// by finding the container whose <c>COOLIFY_FQDN</c> matches and reading the mount at
    /// <see cref="ContainerDataPath"/> — which survives a redeploy, where the container name
    /// does not.
    /// </summary>
    public string? CoolifyFqdn { get; set; }

    /// <summary>Azure subscription containing the production Container App.</summary>
    public string? AzureSubscriptionId { get; set; }

    /// <summary>Resource group containing the production Container App.</summary>
    public string? AzureResourceGroup { get; set; }

    /// <summary>
    /// Container App whose Azure Files-backed volume is mounted at
    /// <see cref="ContainerDataPath"/>.
    /// </summary>
    public string? AzureContainerAppName { get; set; }

    /// <summary>Microsoft Entra tenant used for the interactive production-sync identity.</summary>
    public string? AzureTenantId { get; set; }

    /// <summary>
    /// Optional public-client application id used for interactive browser authentication. When omitted,
    /// the standard Azure CLI public client registration is used without invoking Azure CLI.
    /// </summary>
    public string? AzureAuthenticationClientId { get; set; }

    /// <summary>
    /// User principal name or application client id requested after <see cref="SyncOnStartSwitch"/>.
    /// It is populated from the command line and is not application configuration.
    /// </summary>
    internal string? AzureUsername { get; set; }

    /// <summary>
    /// Optional local-only proxy that receives the same interactive Azure user credential as the
    /// production snapshot reader. The proxy keeps real Azure tokens in the AppHost process while
    /// local applications authenticate with their emulator-issued tokens.
    /// </summary>
    internal AzureUserAccessProxy? AzureUserAccessProxy { get; set; }

    /// <summary>Mount point of the data volume inside the container.</summary>
    public string ContainerDataPath { get; set; } = "/app/user-data";

    /// <summary>Top-level entries not worth copying — analytics spool directories and the like.</summary>
    public List<string> Excludes { get; } = [];

    /// <summary>What to strip out of the copy. Empty is refused; see <see cref="Redact(string, Action{JsonObject})"/>.</summary>
    public List<UserDataRedaction> Redactions { get; } = [];

    /// <summary>Days a fetched copy and its backups are kept before being deleted.</summary>
    public int RetainDays { get; set; } = 7;

    /// <summary>
    /// Azure role definition ids that may be self-activated through PIM when an explicit Azure
    /// production-data fetch initially receives <c>AuthorizationFailed</c>.
    /// </summary>
    public List<string> EligibleAzureRoleDefinitionIds { get; } = [];

    /// <summary>
    /// Maximum number of Azure Files downloads performed concurrently when an incremental
    /// snapshot is assembled through the Azure Files SDK.
    /// </summary>
    public int MaxConcurrentAzureDownloads { get; set; } = 8;

    /// <summary>
    /// Command-line switch that makes the AppHost fetch production data <em>before</em> any
    /// resource starts, so the application boots against it.
    /// <para>
    /// This exists because boot is a code path of its own. Catch-up sweeps, queue drains,
    /// interrupted-run recovery and start-up migrations all run once, at start, against whatever
    /// is already on disk — and the dashboard command can only ever produce that state after the
    /// fact. Passing this switch is the only way to rehearse "new code meets production's data at
    /// start-up", which is what a release actually is.
    /// </para>
    /// <para>
    /// It is read from the process command line and from nowhere else. Configuration, environment
    /// variables and launch profiles are all deliberately excluded: a switch someone can park in
    /// <c>apphost.run.json</c> turns "copy real customer data onto this machine" into a side
    /// effect of starting the solution, which is the one thing this package's design refuses.
    /// </para>
    /// </summary>
    public string SyncOnStartSwitch { get; set; } = "--sync-prod";

    /// <summary>Environment variable carrying the data directory to the app.</summary>
    public string EnvironmentVariable { get; set; } = "USER_DATA_DIR";

    /// <summary>
    /// Configuration key that overrides the local directory — how a test run points the app at
    /// its own isolated copy. Defaults to <c>user-data-dir</c>.
    /// </summary>
    public string LocalPathConfigurationKey { get; set; } = "user-data-dir";

    /// <summary>Production data lives on <paramref name="host"/> at <paramref name="remotePath"/>.</summary>
    public UserDataSyncOptions FromPath(string host, string remotePath)
    {
        Host = host;
        RemotePath = remotePath;

        return this;
    }

    /// <summary>
    /// Production data belongs to the Coolify application served at <paramref name="fqdn"/>,
    /// running on <paramref name="host"/>. The volume is found by asking Docker, so a deploy
    /// that changes the container's name suffix does not break the configuration.
    /// </summary>
    public UserDataSyncOptions FromCoolify(string fqdn, string host)
    {
        Host = host;
        CoolifyFqdn = fqdn;

        return this;
    }

    /// <summary>
    /// Production data is the Azure Files share mounted by an Azure Container App.
    /// The storage account and share are discovered from the deployed resource, then
    /// downloaded with the explicitly authenticated developer identity using OAuth. No storage key
    /// or production application credential is copied to the developer machine.
    /// </summary>
    public UserDataSyncOptions FromAzureContainerApp(
        string subscriptionId,
        string resourceGroup,
        string containerAppName,
        string containerDataPath = "/data")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subscriptionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceGroup);
        ArgumentException.ThrowIfNullOrWhiteSpace(containerAppName);
        ArgumentException.ThrowIfNullOrWhiteSpace(containerDataPath);

        AzureSubscriptionId = subscriptionId;
        AzureResourceGroup = resourceGroup;
        AzureContainerAppName = containerAppName;
        ContainerDataPath = containerDataPath;

        return this;
    }

    /// <summary>
    /// Uses a directly authenticated Microsoft Entra identity for ARM discovery and Azure Files.
    /// A user principal name after <c>--sync-prod</c> selects interactive user authentication with
    /// independently cached tokens and optional PIM activation. An application client id after the
    /// switch selects client-credentials authentication and prompts for its secret in the Aspire
    /// dashboard; application identities require permanent Azure roles and cannot self-activate a
    /// user's PIM assignments. An optional public client id can be supplied when an organisation
    /// owns a dedicated native-client registration for the interactive user flow.
    /// </summary>
    public UserDataSyncOptions AuthenticateAzureUser(string tenantId, string? publicClientId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        if (publicClientId is not null && !Guid.TryParse(publicClientId, out _))
            throw new ArgumentException("The Azure authentication client id must be a GUID.", nameof(publicClientId));

        AzureTenantId = tenantId;
        AzureAuthenticationClientId = publicClientId;
        return this;
    }

    /// <summary>
    /// Makes the interactive Azure identity selected for this sync available to a loopback-only
    /// API proxy. The proxy remains unavailable until an explicit production sync has authenticated.
    /// </summary>
    public UserDataSyncOptions ShareAzureUserWith(AzureUserAccessProxy proxy)
    {
        ArgumentNullException.ThrowIfNull(proxy);
        AzureUserAccessProxy = proxy;
        return this;
    }

    /// <summary>
    /// Allows an explicit Azure production-data fetch to self-activate these eligible PIM roles
    /// for one hour when the first resource read is denied. This is never evaluated merely because
    /// the AppHost starts: it only runs inside the dashboard fetch command or the explicitly typed
    /// <see cref="SyncOnStartSwitch"/> flow.
    /// </summary>
    /// <param name="roleDefinitionIds">
    /// Azure built-in or custom role definition GUIDs. The signed-in developer must already
    /// have eligible assignments for them at the configured resource-group scope.
    /// </param>
    public UserDataSyncOptions ActivateEligibleAzureRoles(params string[] roleDefinitionIds)
    {
        ArgumentNullException.ThrowIfNull(roleDefinitionIds);

        foreach (var roleDefinitionId in roleDefinitionIds)
        {
            if (!Guid.TryParse(roleDefinitionId, out var parsed))
                throw new ArgumentException($"'{roleDefinitionId}' is not a role definition GUID.", nameof(roleDefinitionIds));

            var normalized = parsed.ToString();
            if (!EligibleAzureRoleDefinitionIds.Contains(normalized, StringComparer.OrdinalIgnoreCase))
                EligibleAzureRoleDefinitionIds.Add(normalized);
        }

        return this;
    }

    internal string SourceDisplayName => AzureContainerAppName is not null
        ? $"Azure Container App {AzureResourceGroup}/{AzureContainerAppName}"
        : Host ?? "the configured production source";

    /// <summary>Skips a top-level entry — pass the name as it appears in the data directory.</summary>
    public UserDataSyncOptions Exclude(params string[] entries)
    {
        Excludes.AddRange(entries);

        return this;
    }

    /// <summary>
    /// Rewrites every JSON file matching <paramref name="pathGlob"/> (relative to the data
    /// directory, <c>*</c> matching one path segment) as it lands, so a secret never reaches
    /// disk in usable form.
    /// <para>
    /// At least one redaction is required. There is no sensible default: only the application
    /// knows where it keeps password hashes, tokens and personal data, and a package that
    /// guessed — or that copied everything and let you opt in later — would make "pull
    /// production down" mean "spread production's secrets around" for every app that adopted it.
    /// </para>
    /// </summary>
    /// <param name="pathGlob">e.g. <c>logins/*/credentials.json</c>.</param>
    /// <param name="redact">Mutates the parsed object in place; the file is rewritten if it changed.</param>
    public UserDataSyncOptions Redact(string pathGlob, Action<JsonObject> redact)
    {
        ArgumentNullException.ThrowIfNull(redact);

        return Redact(pathGlob, (_, json) => redact(json));
    }

    /// <summary>
    /// As <see cref="Redact(string, Action{JsonObject})"/>, but the rule also gets the file's
    /// path relative to the data directory.
    /// <para>
    /// A glob usually matches many files, and which one is in hand is frequently the whole
    /// question: <c>organizations/*/billing.json</c> says nothing about <em>which</em>
    /// organisation without the path, because the identity lives in the directory name rather
    /// than in the document.
    /// </para>
    /// </summary>
    /// <param name="pathGlob">e.g. <c>organizations/*/billing.json</c>.</param>
    /// <param name="redact">
    /// Receives the relative path with <c>/</c> separators, then the parsed object to mutate in
    /// place; the file is rewritten if it changed.
    /// </param>
    public UserDataSyncOptions Redact(string pathGlob, Action<string, JsonObject> redact)
    {
        Redactions.Add(new UserDataRedaction(pathGlob, redact));

        return this;
    }
}

/// <summary>One redaction rule: which files, and what to remove from them.</summary>
/// <param name="PathGlob">Path relative to the data directory; <c>*</c> matches one segment.</param>
/// <param name="Redact">
/// Mutation applied to each matching JSON object, given the file's path relative to the data
/// directory.
/// </param>
public sealed record UserDataRedaction(string PathGlob, Action<string, JsonObject> Redact);
