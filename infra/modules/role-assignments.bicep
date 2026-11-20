param appPrincipalId string
param cosmosAccountId string
param cosmosContainerId string
param deployerObjectId string
param keyVaultId string
param storageContainerId string

resource keyVault 'Microsoft.KeyVault/vaults@2026-05-15' existing = {
  name: last(split(keyVaultId, '/'))
}

resource storage 'Microsoft.Storage/storageAccounts@2026-09-01' existing = {
  name: split(storageContainerId, '/')[8]
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2026-09-01' existing = {
  parent: storage
  name: split(storageContainerId, '/')[10]
}

resource storageContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2026-09-01' existing = {
  parent: blobService
  name: last(split(storageContainerId, '/'))
}

resource cosmosAccount 'Microsoft.DocumentDB/databaseAccounts@2026-07-15' existing = {
  name: split(cosmosAccountId, '/')[8]
}

resource cosmosDatabase 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases@2026-07-15' existing = {
  parent: cosmosAccount
  name: split(cosmosContainerId, '/')[10]
}

resource cosmosContainer 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2026-07-15' existing = {
  parent: cosmosDatabase
  name: last(split(cosmosContainerId, '/'))
}

var keyVaultSecretsOfficer = 'b86a8fe4-44ce-4948-aee5-eccb2c155cd7'
var keyVaultSecretsUser = '4633458b-17de-408a-b874-0445c86b69e6'
var storageBlobDataContributor = 'ba92f5b4-2d11-453d-a403-e96b0029c9fe'
var cosmosDataContributor = '00000000-0000-0000-0000-000000000002'

resource deployerKeyVaultRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(keyVaultId, deployerObjectId, keyVaultSecretsOfficer)
  scope: keyVault
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', keyVaultSecretsOfficer)
    principalId: deployerObjectId
    principalType: 'User'
  }
}

resource appKeyVaultRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(keyVaultId, appPrincipalId, keyVaultSecretsUser)
  scope: keyVault
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', keyVaultSecretsUser)
    principalId: appPrincipalId
    principalType: 'ServicePrincipal'
  }
}

resource appStorageRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storageContainerId, appPrincipalId, storageBlobDataContributor)
  scope: storageContainer
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageBlobDataContributor)
    principalId: appPrincipalId
    principalType: 'ServicePrincipal'
  }
}

resource cosmosRole 'Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments@2026-07-15' = {
  name: guid(cosmosContainerId, appPrincipalId, cosmosDataContributor)
  parent: cosmosAccount
  properties: {
    principalId: appPrincipalId
    roleDefinitionId: '${cosmosAccountId}/sqlRoleDefinitions/${cosmosDataContributor}'
    scope: '${cosmosAccountId}/dbs/${cosmosDatabase.name}/colls/${cosmosContainer.name}'
  }
}
