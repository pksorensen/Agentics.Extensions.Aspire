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

    /// <summary>Mount point of the data volume inside the container.</summary>
    public string ContainerDataPath { get; set; } = "/app/user-data";

    /// <summary>Top-level entries not worth copying — analytics spool directories and the like.</summary>
    public List<string> Excludes { get; } = [];

    /// <summary>What to strip out of the copy. Empty is refused; see <see cref="Redact(string, Action{JsonObject})"/>.</summary>
    public List<UserDataRedaction> Redactions { get; } = [];

    /// <summary>Days a fetched copy and its backups are kept before being deleted.</summary>
    public int RetainDays { get; set; } = 7;

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
