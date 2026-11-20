targetScope = 'subscription'

@minLength(1)
@maxLength(64)
param environmentName string

@minLength(1)
param location string

param sessionId string
param deployedBy string
param createdAt string
param deployerObjectId string

var tags = {
  'app-onboard-skill': 'true'
  'app-onboard-session-id': sessionId
  'created-at': createdAt
  environment: environmentName
  'deployed-by': deployedBy
}

resource rg 'Microsoft.Resources/resourceGroups@2026-06-01' = {
  name: 'rg-fireside-prod-5fa8'
  location: location
  tags: tags
}

module logAnalytics './modules/log-analytics.bicep' = {
  name: 'log-analytics'
  scope: rg
  params: {
    location: location
    name: 'log-fireside-prod-5fa8'
    tags: tags
  }
}

module appInsights './modules/app-insights.bicep' = {
  name: 'app-insights'
  scope: rg
  params: {
    location: location
    name: 'appi-fireside-prod-5fa8'
    tags: tags
    workspaceResourceId: logAnalytics.outputs.workspaceId
    keyVaultName: keyVault.outputs.name
  }
}

module keyVault './modules/key-vault.bicep' = {
  name: 'key-vault'
  scope: rg
  params: {
    location: location
    name: 'kv-fireside-prod-5fa8'
    tags: tags
  }
}

module cosmos './modules/cosmos.bicep' = {
  name: 'cosmos'
  scope: rg
  params: {
    location: location
    name: 'cosmos-fireside-prod-5fa8'
    databaseName: 'db-fireside-prod-5fa8'
    containerName: 'events-fireside-prod-5fa8'
    tags: tags
  }
}

module storage './modules/storage.bicep' = {
  name: 'storage'
  scope: rg
  params: {
    location: location
    name: 'stfiresideprod5fa8'
    containerName: 'images-fireside-prod-5fa8'
    tags: tags
  }
}

module signalR './modules/signalr.bicep' = {
  name: 'signalr'
  scope: rg
  params: {
    location: location
    name: 'sig-fireside-prod-5fa8'
    keyVaultName: keyVault.outputs.name
    tags: tags
  }
}

module appService './modules/app-service.bicep' = {
  name: 'app-service'
  scope: rg
  params: {
    location: location
    planName: 'asp-fireside-prod-5fa8'
    appName: 'app-fireside-prod-5fa8'
    keyVaultName: keyVault.outputs.name
    appInsightsSecretName: appInsights.outputs.connectionSecretName
    cosmosEndpoint: cosmos.outputs.endpoint
    cosmosDatabaseName: 'db-fireside-prod-5fa8'
    cosmosContainerName: 'events-fireside-prod-5fa8'
    eventId: 'fireside-prod-5fa8'
    blobServiceUri: storage.outputs.blobServiceUri
    blobContainerName: 'images-fireside-prod-5fa8'
    signalRSecretName: signalR.outputs.connectionSecretName
    tags: tags
  }
}

module roleAssignments './modules/role-assignments.bicep' = {
  name: 'role-assignments'
  scope: rg
  params: {
    appPrincipalId: appService.outputs.principalId
    cosmosAccountId: cosmos.outputs.accountId
    cosmosContainerId: cosmos.outputs.containerId
    deployerObjectId: deployerObjectId
    keyVaultId: keyVault.outputs.id
    storageContainerId: storage.outputs.containerId
  }
}

output resourceGroupName string = rg.name
output appServiceName string = appService.outputs.name
output appServiceHostname string = appService.outputs.defaultHostName
output keyVaultUri string = keyVault.outputs.uri
