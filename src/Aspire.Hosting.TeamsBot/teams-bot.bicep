// Teams notifier — the Azure half of "this project can send Teams notifications".
//
// Creates an Azure Bot with the Microsoft Teams channel switched on. The bot is
// the *sender*: its display name and the Teams app package that points at it
// are what a user sees as "DISCO", "Margin" or whatever the project is called.
//
// Two identity modes, the same split the Margin AppHost already uses:
//
//   UserAssignedMSI  (deployed)  The bot *is* a user-assigned managed identity:
//                                the one passed in `existingIdentityResourceId`
//                                (Margin passes its web app's own identity from
//                                the AppHost) or, if none is passed, a new one
//                                this template creates. No app registration, no
//                                secret. Tokens can only be minted from inside
//                                Azure, by something that has the identity.
//   SingleTenant     (local)     The bot uses an existing app registration
//                                (`existingAppId`) and its client secret. ARM
//                                cannot create app registrations, so that one
//                                is made beforehand. This is the mode for testing
//                                from a laptop.
//
// Nothing here grants Microsoft Graph permissions. Sending to a user who has the
// Teams app installed needs none; installing the app for users on their behalf
// does, and that is IT's step.

@description('Resource name of the bot. Lowercase letters, digits and hyphens.')
@minLength(2)
@maxLength(42)
param botName string

@description('Display name shown as the sender in Teams.')
param displayName string

@description('UserAssignedMSI for deployed environments, SingleTenant to test from a laptop with an app registration.')
@allowed([
  'UserAssignedMSI'
  'SingleTenant'
])
param appType string = 'UserAssignedMSI'

@description('SingleTenant only: Application (client) ID of an existing app registration in this tenant.')
param existingAppId string = ''

@description('HTTPS endpoint that receives Teams activities (installs, replies). A notification-only test can leave the placeholder; sending does not use it.')
param messagingEndpoint string = 'https://example.invalid/api/teams/messages'

@description('Region for the managed identity. The bot itself is always global.')
param location string = resourceGroup().location

@description('F0 is free and has no limit on Teams messages; S1 is only needed for premium channels.')
@allowed([
  'F0'
  'S1'
])
param sku string = 'F0'

@description('UserAssignedMSI only: resource id of an identity that already exists, e.g. the identity of the app that will send. Empty = create one.')
param existingIdentityResourceId string = ''

@description('UserAssignedMSI only: client id of that identity. Required with existingIdentityResourceId.')
param existingIdentityClientId string = ''

param tags object = {}

var useMsi = appType == 'UserAssignedMSI'
var createIdentity = useMsi && empty(existingIdentityResourceId)

resource identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = if (createIdentity) {
  name: 'id-${botName}'
  location: location
  tags: tags
}

resource bot 'Microsoft.BotService/botServices@2022-09-15' = {
  name: botName
  location: 'global'
  kind: 'azurebot'
  sku: {
    name: sku
  }
  tags: tags
  properties: {
    displayName: displayName
    endpoint: messagingEndpoint
    msaAppType: appType
    msaAppId: createIdentity ? identity!.properties.clientId : (useMsi ? existingIdentityClientId : existingAppId)
    msaAppTenantId: tenant().tenantId
    msaAppMSIResourceId: createIdentity ? identity.id : (useMsi ? existingIdentityResourceId : null)
    disableLocalAuth: false
  }
}

resource teams 'Microsoft.BotService/botServices/channels@2022-09-15' = {
  parent: bot
  name: 'MsTeamsChannel'
  location: 'global'
  properties: {
    channelName: 'MsTeamsChannel'
    properties: {
      isEnabled: true
    }
  }
}

@description('Goes into the Teams app manifest as bots[0].botId and webApplicationInfo.id.')
output botAppId string = bot.properties.msaAppId

output tenantId string = tenant().tenantId

@description('Assign this identity to the Container App that sends notifications (UserAssignedMSI mode).')
output identityResourceId string = createIdentity ? identity.id : (useMsi ? existingIdentityResourceId : '')

output identityClientId string = createIdentity ? identity!.properties.clientId : (useMsi ? existingIdentityClientId : '')
