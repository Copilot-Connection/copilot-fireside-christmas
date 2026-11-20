param location string
param name string
param databaseName string
param containerName string
param tags object

resource account 'Microsoft.DocumentDB/databaseAccounts@2026-07-15' = {
  name: name
  location: location
  tags: tags
  kind: 'GlobalDocumentDB'
  properties: {
    databaseAccountOfferType: 'Standard'
    capacityMode: 'Serverless'
    disableLocalAuth: true
    enableAutomaticFailover: false
    consistencyPolicy: {
      defaultConsistencyLevel: 'Strong'
    }
    locations: [
      {
        locationName: location
        failoverPriority: 0
        isZoneRedundant: false
      }
    ]
  }
}

resource database 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases@2026-07-15' = {
  parent: account
  name: databaseName
  properties: {
    resource: {
      id: databaseName
    }
  }
}

resource container 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2026-07-15' = {
  parent: database
  name: containerName
  properties: {
    resource: {
      id: containerName
      partitionKey: {
        paths: [
          '/eventId'
        ]
        kind: 'Hash'
      }
      indexingPolicy: {
        automatic: true
        indexingMode: 'consistent'
      }
    }
  }
}

output accountId string = account.id
output containerId string = container.id
output endpoint string = account.properties.documentEndpoint
