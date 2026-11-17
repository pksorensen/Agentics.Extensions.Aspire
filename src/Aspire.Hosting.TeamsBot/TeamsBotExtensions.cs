using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure;
using Aspire.Hosting.Pipelines;
using Aspire.Hosting.TeamsBot;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting;

/// <summary>
/// An Azure Bot with the Microsoft Teams channel, used as the sender of an app's
/// Teams notifications, together with the Teams app package that gives it its
/// name and icon in Teams.
/// </summary>
public sealed class TeamsBotResource : AzureBicepResource
{
    internal TeamsBotResource(string name, string bicep, TeamsAppDefinition app, string? environment)
        : base(name, templateString: bicep)
    {
        App = app;
        Environment = environment;
        Scopes = app.Scopes;
    }

    /// <summary>The Teams app definition the package is built from.</summary>
    public TeamsAppDefinition App { get; }

    /// <summary>Deployment environment (<c>null</c> or <c>prod</c> = production names).</summary>
    public string? Environment { get; }

    /// <summary>The bot's app id: the managed identity's client id, or the app registration's.</summary>
    public BicepOutputReference BotAppId => new("botAppId", this);

    /// <summary>The tenant the bot belongs to.</summary>
    public BicepOutputReference TenantId => new("tenantId", this);

    /// <summary>The URL Teams posts activities to, when set with <c>WithMessagingEndpoint</c>.</summary>
    public string? MessagingEndpoint { get; internal set; }

    /// <summary>Where the Teams app can be added: <c>teams-app.json</c>'s <c>"scopes"</c>
    /// unless the AppHost sets them with <c>WithScopes</c>.</summary>
    public TeamsBotScopes Scopes { get; internal set; }

    /// <summary>Name of the pipeline step that writes the Teams app package.</summary>
    public string PackageStepName => $"teams-package-{Name}";
}

/// <summary>Extension methods for adding a Teams notification bot to an AppHost.</summary>
public static class TeamsBotExtensions
{
    /// <summary>
    /// Adds an Azure Bot with the Teams channel, named from <paramref name="teamsAppPath"/>
    /// (a <c>teams-app.json</c> beside its two icons). On <c>aspire deploy</c> the
    /// Teams app package for the deployed bot is written to
    /// <c>aspire-output/&lt;name&gt;/</c> and listed in the deploy summary.
    /// </summary>
    /// <param name="builder">The AppHost builder.</param>
    /// <param name="name">Aspire resource name.</param>
    /// <param name="teamsAppPath">Path to <c>teams-app.json</c>, relative to the AppHost directory.</param>
    /// <param name="environment">Deployment environment. Anything other than empty or <c>prod</c>
    /// gives the bot a <c>-&lt;env&gt;</c> suffix and the Teams app its own id, so environments
    /// do not overwrite each other in the tenant's app catalog.</param>
    public static IResourceBuilder<TeamsBotResource> AddTeamsBot(
        this IDistributedApplicationBuilder builder,
        [ResourceName] string name,
        string teamsAppPath,
        string? environment = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(teamsAppPath);

        var app = TeamsAppDefinition.Load(Path.Combine(builder.AppHostDirectory, teamsAppPath));
        var names = app.NamesFor(environment);
        var env = TeamsAppDefinition.IsProduction(environment) ? null : environment;

        builder.AddAzureProvisioning();
        var resource = new TeamsBotResource(name, ReadTemplate(), app, env);
        resource.Annotations.Add(new PipelineStepAnnotation(_ =>
        {
            var step = new PipelineStep
            {
                Name = resource.PackageStepName,
                Description = $"Writes the Teams app package for {names.BotName}.",
                Action = ctx => WritePackageAsync(ctx, resource),
            };
            step.DependsOn($"provision-{name}");
            step.RequiredBy(WellKnownPipelineSteps.Deploy);
            return step;
        }));

        return builder.AddResource(resource)
            .WithParameter("botName", names.BotName)
            .WithParameter("displayName", names.ShortName)
            .WithParameter("appType", "UserAssignedMSI");
    }

    /// <summary>
    /// Makes the bot <i>be</i> an existing user-assigned identity, normally the one
    /// the sending app already runs as. No app registration, no secret, and no
    /// second identity on the app. Without this the template creates an identity of
    /// its own, which must then be assigned to the sender.
    /// </summary>
    public static IResourceBuilder<TeamsBotResource> WithIdentity(
        this IResourceBuilder<TeamsBotResource> builder,
        IResourceBuilder<AzureUserAssignedIdentityResource> identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return builder
            .WithParameter("existingIdentityResourceId", identity.Resource.Id)
            .WithParameter("existingIdentityClientId", identity.Resource.ClientId);
    }

    /// <summary>
    /// Sets the HTTPS endpoint Teams posts the bot's activities to: messages people
    /// write to it, installs. Required when <c>teams-app.json</c> says
    /// <c>"conversational": true</c>; without it the deploy fails rather than ship
    /// a bot that shows a compose box and never answers.
    /// </summary>
    /// <param name="builder">The bot.</param>
    /// <param name="endpointUrl">Absolute <c>https://</c> URL, e.g. <c>https://web.example/api/teams/messages</c>.</param>
    public static IResourceBuilder<TeamsBotResource> WithMessagingEndpoint(
        this IResourceBuilder<TeamsBotResource> builder,
        string endpointUrl)
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (!Uri.TryCreate(endpointUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException($"Messaging endpoint must be an absolute https URL (got '{endpointUrl}').", nameof(endpointUrl));
        }

        builder.Resource.MessagingEndpoint = uri.ToString();
        return builder.WithParameter("messagingEndpoint", uri.ToString());
    }

    /// <summary>
    /// Sets where the Teams app can be added: the personal chat always, plus group
    /// chats and/or a team's channels. Overrides <c>"scopes"</c> in <c>teams-app.json</c>.
    /// In a group chat or channel the bot only receives messages that @mention it,
    /// and everyone there reads its answers.
    /// </summary>
    public static IResourceBuilder<TeamsBotResource> WithScopes(
        this IResourceBuilder<TeamsBotResource> builder,
        TeamsBotScopes scopes)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Resource.Scopes = scopes | TeamsBotScopes.Personal;
        return builder;
    }

    /// <summary>Lets the app be added to group chats, a chat between two people included.</summary>
    public static IResourceBuilder<TeamsBotResource> WithGroupChats(this IResourceBuilder<TeamsBotResource> builder) =>
        builder.WithScopes(builder.Resource.Scopes | TeamsBotScopes.GroupChat);

    /// <summary>Lets the app be added to a team, and so to its channels.</summary>
    public static IResourceBuilder<TeamsBotResource> WithChannels(this IResourceBuilder<TeamsBotResource> builder) =>
        builder.WithScopes(builder.Resource.Scopes | TeamsBotScopes.Team);

    /// <summary>
    /// Gives a sender <c>TEAMS_BOT_APP_ID</c> and <c>TEAMS_TENANT_ID</c>. With the bot on the
    /// sender's own identity, that is all it needs to mint a Bot Framework token.
    /// <para>
    /// Also <c>TEAMS_APP_MANIFEST</c>: the Teams app's <c>manifest.json</c> as this
    /// package builds it (base64 UTF-8 JSON), with <c>${TEAMS_BOT_APP_ID}</c> where the
    /// bot id goes. An app that offers its Teams app for download zips that with the
    /// two icons instead of building a manifest of its own, so the package people
    /// upload is the one the AppHost describes.
    /// </para>
    /// </summary>
    public static IResourceBuilder<T> WithTeamsBot<T>(this IResourceBuilder<T> builder, IResourceBuilder<TeamsBotResource> bot)
        where T : IResourceWithEnvironment
    {
        ArgumentNullException.ThrowIfNull(bot);
        return builder
            .WithEnvironment("TEAMS_BOT_APP_ID", bot.Resource.BotAppId)
            .WithEnvironment("TEAMS_TENANT_ID", bot.Resource.TenantId)
            // A callback, so scopes set after this call still count.
            .WithEnvironment(context =>
            {
                var manifest = bot.Resource.App.BuildManifest(
                    TeamsAppDefinition.BotAppIdPlaceholder, bot.Resource.Environment, bot.Resource.Scopes);
                context.EnvironmentVariables["TEAMS_APP_MANIFEST"] =
                    Convert.ToBase64String(TeamsAppDefinition.ManifestBytes(manifest));
            });
    }

    private static async Task WritePackageAsync(PipelineStepContext ctx, TeamsBotResource bot)
    {
        if (bot.App.Conversational && bot.MessagingEndpoint is null)
        {
            throw new InvalidOperationException(
                $"Bot '{bot.Name}' is conversational (teams-app.json) but has no messaging endpoint. Call WithMessagingEndpoint(...).");
        }

        var botAppId = await bot.BotAppId.GetValueAsync(ctx.CancellationToken).ConfigureAwait(false);
        if (!Guid.TryParse(botAppId, out _))
        {
            throw new InvalidOperationException($"Bot '{bot.Name}' has no app id after provisioning (got '{botAppId}').");
        }

        var names = bot.App.NamesFor(bot.Environment);
        var manifest = bot.App.BuildManifest(botAppId!, bot.Environment, bot.Scopes);

        var output = ctx.Services.GetRequiredService<IPipelineOutputService>().GetOutputDirectory(bot);
        Directory.CreateDirectory(output);
        var zipPath = Path.Combine(output, $"{names.BotName}.zip");
        await File.WriteAllBytesAsync(zipPath, bot.App.BuildPackage(manifest), ctx.CancellationToken).ConfigureAwait(false);
        await File.WriteAllBytesAsync(
            Path.Combine(output, $"{names.BotName}.manifest.json"),
            TeamsAppDefinition.ManifestBytes(manifest),
            ctx.CancellationToken).ConfigureAwait(false);

        ctx.Logger.LogInformation(
            "Teams app package for '{ShortName}' (Teams app {TeamsAppId}, bot {BotAppId}): {Path}",
            names.ShortName, names.TeamsAppId, botAppId, zipPath);
        ctx.Summary.Add("Teams app package", zipPath);
    }

    private static string ReadTemplate()
    {
        using var stream = typeof(TeamsBotResource).Assembly.GetManifestResourceStream("Aspire.Hosting.TeamsBot.teams-bot.bicep")
            ?? throw new InvalidOperationException("Embedded teams-bot.bicep is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
