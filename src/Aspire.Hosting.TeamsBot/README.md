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

## Environments

Any `environment` other than empty or `prod` gets these suffixes:

- the bot: `<botName>-<env>`
- the Teams name: ` (<env>)`
- its own Teams app id, derived stably from `appId`

So demo and prod can live side by side in the tenant's app catalog. If the
suffix would push the short name past 30 characters, the environment appears
in the full name only.

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
