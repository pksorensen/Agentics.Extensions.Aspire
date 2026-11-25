# Agentics.Extensions.Aspire.TeamsBot

Lets an app send Microsoft Teams notifications under its own name ("Margin",
"DISCO", …). One call in the AppHost covers both halves:

- **Azure:** an Azure Bot (`Microsoft.BotService/botServices`, F0) with the Teams
  channel switched on. The bot *is* the sender's user-assigned managed identity,
  so there is no app registration and no secret.
- **Teams:** on `aspire deploy`, the Teams app package for the bot that was just
  deployed is written to `aspire-output/<resource>/<bot>.zip` and listed in the
  deploy summary. This zip is what you upload in Teams or hand to IT.

```csharp
var webIdentity = builder.AddAzureUserAssignedIdentity("web-identity");
web.WithAzureUserAssignedIdentity(webIdentity);

var bot = builder.AddTeamsBot("teams-bot", "teams/teams-app.json", environment: "demo")
    .WithIdentity(webIdentity);

web.WithTeamsBot(bot);   // TEAMS_BOT_APP_ID + TEAMS_TENANT_ID
```

## teams-app.json

The file sits beside its two icons. Its paths are relative to the file itself.

```json
{
  "appId": "<a GUID you generate once and never change>",
  "version": "1.0.0",
  "botName": "bot-myapp-notify",
  "name": { "short": "MyApp", "full": "MyApp — notifications" },
  "description": { "short": "≤ 80 chars", "full": "≤ 4000 chars" },
  "developer": { "name": "…", "websiteUrl": "https://…", "privacyUrl": "https://…", "termsOfUseUrl": "https://…" },
  "accentColor": "#4400cc",
  "validDomains": [],
  "icons": { "color": "color.png", "outline": "outline.png" }
}
```

- `color.png` is 192×192. `outline.png` is 32×32, white on transparent.
- `name.short` is the sender's name in every chat. Teams caps it at 30
  characters.

## Group chats and channels

The app goes in the personal chat only, unless you say otherwise:

```csharp
var bot = builder.AddTeamsBot("teams-bot", "teams/teams-app.json", environment)
    .WithIdentity(webIdentity)
    .WithGroupChats()   // group chats, a chat between two people included
    .WithChannels();    // a team's channels
```

`WithScopes(TeamsBotScopes.Personal | TeamsBotScopes.GroupChat)` says the same in
one call, and `"scopes": ["personal", "groupChat", "team"]` in `teams-app.json`
is the default the AppHost starts from. The command menu follows the scopes.
`WithChannels()` covers private and shared channels as well: the manifest then
declares `"supportsChannelFeatures": "tier1"`, which Teams requires before it
offers an app there. Such a channel is added to one at a time, after the app is
in the team.

In a group chat or a channel the bot only receives messages that @mention it,
and everyone there reads its answers: an app that answers with a person's own
data should keep that to the personal chat (`conversation.conversationType`).

## Reading the conversation

`.WithChatHistory()` lets the bot read the group chats and channels it is in:
Teams then delivers every message there, not only those that @mention it, and
the bot's own identity can list the history through Graph
(`GET /chats/{id}/messages`, `GET /teams/{id}/channels/{id}/messages`). The
manifest asks for `ChatMessage.Read.Chat` and `ChannelMessage.Read.Group` as
resource-specific consent, which the chat's or team's owner grants when the app
is added or updated, and only for that chat or team. The bot should go on
answering only when it is @mentioned.

## Offering the package from the app

`WithTeamsBot` also gives the app `TEAMS_APP_MANIFEST`: the `manifest.json` this
package builds (base64 UTF-8 JSON) with `${TEAMS_BOT_APP_ID}` where the bot id
goes. An app with a "download the Teams app" link replaces the placeholder with
`TEAMS_BOT_APP_ID` and zips it with the two icons, so the package people upload
is the one the AppHost describes, never a second copy of these rules.

## Conversational bots

By default the bot is notification-only: Teams shows no compose box. To let
people write to it, set `"conversational": true` in `teams-app.json`, bump
`version`, and give the bot an endpoint for the activities Teams posts:

```csharp
var bot = builder.AddTeamsBot("teams-bot", "teams/teams-app.json", environment)
    .WithIdentity(webIdentity)
    .WithMessagingEndpoint("https://web.example.net/api/teams/messages");
```

A conversational bot without an endpoint fails `aspire deploy` in the package
step. The endpoint is a plain URL, not a reference to the web resource: the web
app already consumes the bot's outputs, and the reverse reference would be a
cycle. The receiving app must verify the Bot Connector's JWT itself (issuer
`https://api.botframework.com`, audience = bot app id, `serviceUrl` claim, key
endorsement for the channel).

A conversational bot can also list its commands. Teams shows them in a menu by
the compose box, and choosing one sends its title to the bot as an ordinary
message:

```json
"commands": [
  { "title": "portfolio", "description": "Your portfolios with customers and projects" },
  { "title": "team", "description": "Your team and its allocation this month" }
]
```

At most 10 are used; a title is cut at 32 characters and a description at 128.
Bump `version` and re-upload the package when the list changes.

## Environments

Any `environment` other than empty or `prod` gets these suffixes:

- the bot: `<botName>-<env>`
- the Teams name: ` (<env>)`
- its own Teams app id, derived stably from `appId`

So demo and prod can live side by side in the tenant's app catalog. If the
suffix would push the short name past 30 characters, the environment appears
in the full name only.

### Your own name

`WithName` names the Teams app outright, replacing `teams-app.json`'s name and
the environment suffix, for example so people can tell a demo bot from
production at a glance:

```csharp
var bot = builder.AddTeamsBot("teams-bot", "teams/teams-app.json", environment);
if (environment != "prod")
    bot.WithName("Kant DEMO", "Kant DEMO — test environment");
```

The short name is at most 30 characters and the full name at most 100; the
full name defaults to the short one. The bot's Azure name and the Teams app id
still follow the environment, so both apps keep living side by side. Bump
`version` and re-upload the package after a rename.

## Pipeline

| Step | Runs after | Does |
|---|---|---|
| `provision-<name>` | `create-provisioning-context` | Deploys the bot (Aspire's standard Bicep step) |
| `teams-package-<name>` | `provision-<name>` | Reads `botAppId`, writes `<bot>.zip` + `<bot>.manifest.json` |

`aspire do teams-package-<name>` rebuilds the package against an existing
deployment.

The package changes only when `teams-app.json` or the icons change. In that
case, bump `version`: Teams only accepts an update with a higher version.

## Before the first deploy

- The subscription must have `Microsoft.BotService` registered:
  `az provider register -n Microsoft.BotService`.
- Sending needs no Graph permissions. The recipient must have the app
  installed, otherwise creating the conversation fails with
  `403 ForbiddenOperationException`.
- Approval is IT's step. They upload the zip in Teams Admin Center → Manage apps,
  or allow custom app upload and push the app via a setup policy.
