param location string
param name string
param keyVaultName string
param tags object

resource signalR 'Microsoft.SignalRService/signalR@2024-03-01' = {
  name: name
  location: location
  tags: tags
  sku: {
    name: 'Standard_S1'
    tier: 'Standard'
    capacity: 1
  }
  kind: 'SignalR'
  properties: {
    features: [
      {
        flag: 'ServiceMode'
        value: 'Default'
      }
    ]
  }
}

resource signalRConnectionSecret 'Microsoft.KeyVault/vaults/secrets@2026-05-15' = {
  name: 'signalr-connection'
  parent: keyVault
  properties: {
    value: signalR.listKeys().primaryConnectionString
  }
}

resource keyVault 'Microsoft.KeyVault/vaults@2026-05-15' existing = {
  name: keyVaultName
}

output connectionSecretName string = 'signalr-connection'
output id string = signalR.id
