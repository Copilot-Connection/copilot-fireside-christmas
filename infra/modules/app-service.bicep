param location string
param planName string
param appName string
param keyVaultName string
param appInsightsSecretName string
param cosmosEndpoint string
param cosmosDatabaseName string
param cosmosContainerName string
param eventId string
param blobServiceUri string
param blobContainerName string
param signalRSecretName string
param tags object

resource plan 'Microsoft.Web/serverfarms@2026-08-01' = {
  name: planName
  location: location
  tags: tags
  kind: 'linux'
  sku: {
    name: 'B1'
    tier: 'Basic'
    size: 'B1'
    family: 'B'
    capacity: 1
  }
  properties: {
    reserved: true
  }
}

resource app 'Microsoft.Web/sites@2026-08-01' = {
  name: appName
  location: location
  tags: tags
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    siteConfig: {
      linuxFxVersion: 'DOTNETCORE|10.0'
      ftpsState: 'Disabled'
      minTlsVersion: '1.2'
      alwaysOn: true
      webSocketsEnabled: true
      healthCheckPath: '/health'
      appCommandLine: 'dotnet Fireside.dll'
    }
  }
}

resource appSettings 'Microsoft.Web/sites/config@2026-08-01' = {
  parent: app
  name: 'appsettings'
  properties: {
    ASPNETCORE_ENVIRONMENT: 'Production'
    AllowedHosts: app.properties.defaultHostName
    Demo__Enabled: 'false'
    Database__Provider: 'Cosmos'
    Cosmos__Endpoint: cosmosEndpoint
    Cosmos__Database: cosmosDatabaseName
    Cosmos__Container: cosmosContainerName
    Cosmos__EventId: eventId
    Storage__BlobServiceUri: blobServiceUri
    Storage__Container: blobContainerName
    AzureAd__TenantId: subscription().tenantId
    AzureAd__ClientId: '@Microsoft.KeyVault(VaultName=${keyVaultName};SecretName=entra-client-id)'
    AzureAd__ClientSecret: '@Microsoft.KeyVault(VaultName=${keyVaultName};SecretName=entra-client-secret)'
    ConnectionStrings__AzureSignalR: '@Microsoft.KeyVault(VaultName=${keyVaultName};SecretName=${signalRSecretName})'
    DataProtection__KeyPath: '/home/fireside-keys'
    ASPNETCORE_FORWARDEDHEADERS_ENABLED: 'true'
    SCM_DO_BUILD_DURING_DEPLOYMENT: 'false'
    ENABLE_ORYX_BUILD: 'false'
    WEBSITE_RUN_FROM_PACKAGE: '1'
    WEBSITES_ENABLE_APP_SERVICE_STORAGE: 'true'
    APPLICATIONINSIGHTS_CONNECTION_STRING: '@Microsoft.KeyVault(VaultName=${keyVaultName};SecretName=${appInsightsSecretName})'
    ApplicationInsightsAgent_EXTENSION_VERSION: '~3'
    XDT_MicrosoftApplicationInsights_Mode: 'recommended'
    XDT_MicrosoftApplicationInsights_PreemptSdk: '1'
    APPINSIGHTS_JAVASCRIPT_ENABLED: 'false'
  }
}

resource scmAuth 'Microsoft.Web/sites/basicPublishingCredentialsPolicies@2026-08-01' = {
  parent: app
  name: 'scm'
  properties: {
    allow: true
  }
}

resource ftpAuth 'Microsoft.Web/sites/basicPublishingCredentialsPolicies@2026-08-01' = {
  parent: app
  name: 'ftp'
  properties: {
    allow: false
  }
}

output id string = app.id
output name string = app.name
output principalId string = app.identity.principalId
output defaultHostName string = app.properties.defaultHostName
