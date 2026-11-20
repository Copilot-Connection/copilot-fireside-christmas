param location string
param name string
param workspaceResourceId string
param tags object
param keyVaultName string

resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: name
  location: location
  tags: tags
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: workspaceResourceId
  }
}

resource keyVault 'Microsoft.KeyVault/vaults@2026-05-15' existing = {
  name: keyVaultName
}

resource connectionSecret 'Microsoft.KeyVault/vaults/secrets@2026-05-15' = {
  parent: keyVault
  name: 'appinsights-connection'
  properties: {
    value: appInsights.properties.ConnectionString
  }
}

output connectionSecretName string = connectionSecret.name
output id string = appInsights.id
