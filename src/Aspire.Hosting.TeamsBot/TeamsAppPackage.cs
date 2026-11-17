using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Aspire.Hosting.TeamsBot;

/// <summary>
/// The project's Teams app definition (<c>teams-app.json</c>) and its two icons,
/// read once when the AppHost starts so a missing icon fails the build of the
/// model rather than the deploy.
/// </summary>
public sealed class TeamsAppDefinition
{
    /// <summary>Teams manifest schema version the package is written against.</summary>
    public const string ManifestVersion = "1.22";

    /// <summary><c>name.short</c> is capped by the Teams manifest schema.</summary>
    public const int ShortNameMax = 30;

    private TeamsAppDefinition(JsonObject config, string directory, byte[] colorIcon, byte[] outlineIcon)
    {
        Config = config;
        Directory = directory;
        ColorIcon = colorIcon;
        OutlineIcon = outlineIcon;
        AppId = Guid.Parse(Required("appId"));
        BotName = Required("botName");
        ShortName = config["name"]?["short"]?.GetValue<string>() ?? throw Missing("name.short");
        FullName = config["name"]?["full"]?.GetValue<string>() ?? ShortName;
        Conversational = config["conversational"]?.GetValue<bool>() ?? false;
        Scopes = ScopesFrom(config["scopes"] as JsonArray);
    }

    /// <summary>
    /// Where the app can be added, from <c>"scopes"</c> in <c>teams-app.json</c>
    /// (default: the personal chat only). <c>WithScopes</c> in the AppHost wins.
    /// </summary>
    public TeamsBotScopes Scopes { get; }

    /// <summary>The raw <c>teams-app.json</c>.</summary>
    public JsonObject Config { get; }

    /// <summary>Directory the icons were read from.</summary>
    public string Directory { get; }

    /// <summary>The Teams app's own id in production. Keep it stable forever.</summary>
    public Guid AppId { get; }

    /// <summary>Azure resource name of the production bot.</summary>
    public string BotName { get; }

    /// <summary>The sender name people see in Teams.</summary>
    public string ShortName { get; }

    /// <summary>The long name shown in the app's details.</summary>
    public string FullName { get; }

    /// <summary>
    /// <c>true</c> when people can write to the bot (<c>"conversational": true</c>).
    /// It then needs a messaging endpoint; <c>false</c> makes it notification-only.
    /// </summary>
    public bool Conversational { get; }

    /// <summary>192×192 colour icon.</summary>
    public byte[] ColorIcon { get; }

    /// <summary>32×32 white-on-transparent outline icon.</summary>
    public byte[] OutlineIcon { get; }

    /// <summary>Reads <c>teams-app.json</c> and the icons it names (paths relative to the file).</summary>
    public static TeamsAppDefinition Load(string path)
    {
        var full = Path.GetFullPath(path);
        if (!File.Exists(full))
        {
            throw new FileNotFoundException($"Teams app definition not found: {full}", full);
        }

        var config = JsonNode.Parse(File.ReadAllText(full))?.AsObject()
            ?? throw new InvalidDataException($"{full} is not a JSON object.");
        var dir = Path.GetDirectoryName(full)!;
        var icons = config["icons"];
        var color = icons?["color"]?.GetValue<string>() ?? "color.png";
        var outline = icons?["outline"]?.GetValue<string>() ?? "outline.png";
        return new TeamsAppDefinition(
            config,
            dir,
            File.ReadAllBytes(Path.Combine(dir, color)),
            File.ReadAllBytes(Path.Combine(dir, outline)));
    }

    /// <summary>Environment-qualified names. Production (or no environment) gets the bare names.</summary>
    public TeamsAppNames NamesFor(string? environment)
    {
        var env = IsProduction(environment) ? null : environment;
        if (env is null)
        {
            return new TeamsAppNames(BotName, ShortName, FullName, AppId);
        }

        var suffix = $" ({env})";
        // The short name is the sender in every chat. If the suffix would push it
        // past the schema limit, truncating would mangle the name; the environment
        // then shows in the full name only.
        var shortName = ShortName.Length + suffix.Length <= ShortNameMax ? ShortName + suffix : ShortName;
        return new TeamsAppNames($"{BotName}-{env}", shortName, Truncate(FullName + suffix, 100), DeriveGuid(AppId, env));
    }

    /// <summary>The placeholder <see cref="BuildManifest"/> puts where the bot id goes when
    /// the receiving app fills it in from <c>TEAMS_BOT_APP_ID</c> at run time.</summary>
    public const string BotAppIdPlaceholder = "${TEAMS_BOT_APP_ID}";

    /// <summary>Builds <c>manifest.json</c> for a bot app id and environment.</summary>
    public JsonObject BuildManifest(string botAppId, string? environment, TeamsBotScopes? scopes = null)
    {
        var effective = scopes ?? Scopes;
        var names = NamesFor(environment);
        return new JsonObject
        {
            ["$schema"] = $"https://developer.microsoft.com/json-schemas/teams/v{ManifestVersion}/MicrosoftTeams.schema.json",
            ["manifestVersion"] = ManifestVersion,
            ["version"] = Config["version"]?.GetValue<string>() ?? "1.0.0",
            ["id"] = names.TeamsAppId.ToString(),
            ["developer"] = Config["developer"]?.DeepClone(),
            ["name"] = new JsonObject
            {
                ["short"] = Truncate(names.ShortName, ShortNameMax),
                ["full"] = names.FullName,
            },
            ["description"] = Config["description"]?.DeepClone(),
            ["icons"] = new JsonObject { ["color"] = "color.png", ["outline"] = "outline.png" },
            ["accentColor"] = Config["accentColor"]?.GetValue<string>() ?? "#FFFFFF",
            ["bots"] = new JsonArray { BotEntry(botAppId, effective) },
            ["permissions"] = new JsonArray("identity"),
            ["validDomains"] = Config["validDomains"]?.DeepClone() ?? new JsonArray(),
        };
    }

    private JsonObject BotEntry(string botAppId, TeamsBotScopes scopes)
    {
        var bot = new JsonObject
        {
            ["botId"] = botAppId,
            ["scopes"] = ScopeNames(scopes),
            // Not conversational: one-way, Teams shows no compose box, so
            // nobody expects an answer from a bot with nothing behind it.
            ["isNotificationOnly"] = !Conversational,
            ["supportsFiles"] = false,
        };
        // The command menu Teams shows by the compose box. Only a
        // conversational bot has a compose box to show it in.
        if (Conversational && Config["commands"] is JsonArray commands && CommandsFor(commands) is { Count: > 0 } list)
        {
            bot["commandLists"] = new JsonArray(new JsonObject
            {
                ["scopes"] = ScopeNames(scopes),
                ["commands"] = list,
            });
        }
        return bot;
    }

    /// <summary>
    /// <c>commands</c> from <c>teams-app.json</c> as manifest commands: at most 10,
    /// title ≤ 32 and description ≤ 128 characters, as the schema allows.
    /// </summary>
    private static JsonArray CommandsFor(JsonArray commands)
    {
        var result = new JsonArray();
        foreach (var command in commands.OfType<JsonObject>().Take(10))
        {
            var title = command["title"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(title)) continue;
            result.Add(new JsonObject
            {
                ["title"] = Truncate(title, 32),
                ["description"] = Truncate(command["description"]?.GetValue<string>() ?? title, 128),
            });
        }
        return result;
    }

    /// <summary>The manifest's scope names, personal always first.</summary>
    private static JsonArray ScopeNames(TeamsBotScopes scopes)
    {
        var names = new JsonArray("personal");
        if (scopes.HasFlag(TeamsBotScopes.GroupChat)) names.Add("groupChat");
        if (scopes.HasFlag(TeamsBotScopes.Team)) names.Add("team");
        return names;
    }

    private static TeamsBotScopes ScopesFrom(JsonArray? names)
    {
        var scopes = TeamsBotScopes.Personal;
        foreach (var name in names?.Select(node => node?.GetValue<string>()) ?? [])
        {
            scopes |= name switch
            {
                "personal" => TeamsBotScopes.Personal,
                "groupChat" => TeamsBotScopes.GroupChat,
                "team" => TeamsBotScopes.Team,
                _ => throw new InvalidDataException($"teams-app.json: unknown scope '{name}' (personal, groupChat, team)."),
            };
        }
        return scopes;
    }

    /// <summary>Builds the zip Teams accepts: manifest and icons at the root.</summary>
    public byte[] BuildPackage(JsonObject manifest)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(archive, "manifest.json", ManifestBytes(manifest));
            Add(archive, "color.png", ColorIcon);
            Add(archive, "outline.png", OutlineIcon);
        }

        return buffer.ToArray();
    }

    /// <summary>UTF-8 manifest text as written into the package.</summary>
    public static byte[] ManifestBytes(JsonObject manifest) =>
        Encoding.UTF8.GetBytes(manifest.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }) + "\n");

    /// <summary>Stable, version-5-shaped GUID derived from a base GUID and a label (same as generate.mjs).</summary>
    public static Guid DeriveGuid(Guid baseId, string label)
    {
        var h = SHA1.HashData(Encoding.UTF8.GetBytes($"{baseId.ToString().ToLowerInvariant()}:{label}"));
        h[6] = (byte)((h[6] & 0x0f) | 0x50);
        h[8] = (byte)((h[8] & 0x3f) | 0x80);
        var x = Convert.ToHexString(h, 0, 16).ToLowerInvariant();
        return Guid.Parse($"{x[..8]}-{x[8..12]}-{x[12..16]}-{x[16..20]}-{x[20..]}");
    }

    internal static bool IsProduction(string? environment) =>
        string.IsNullOrWhiteSpace(environment) || string.Equals(environment, "prod", StringComparison.OrdinalIgnoreCase);

    private static void Add(ZipArchive archive, string name, byte[] data)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        // Fixed timestamp: the same input gives the same bytes.
        entry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
        using var stream = entry.Open();
        stream.Write(data);
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    private string Required(string key) =>
        Config[key]?.GetValue<string>() is { Length: > 0 } v ? v : throw Missing(key);

    private static InvalidDataException Missing(string key) => new($"teams-app.json: '{key}' is required.");
}

/// <summary>Names for one environment's bot and Teams app.</summary>
public sealed record TeamsAppNames(string BotName, string ShortName, string FullName, Guid TeamsAppId);

/// <summary>Where a Teams app can be added. The personal chat is always included.</summary>
[Flags]
public enum TeamsBotScopes
{
    /// <summary>The person's own 1:1 chat with the bot.</summary>
    Personal = 1,
    /// <summary>Group chats, including a chat between two people. The bot hears @mentions only.</summary>
    GroupChat = 2,
    /// <summary>A team's channels. The bot hears @mentions only.</summary>
    Team = 4,
}
