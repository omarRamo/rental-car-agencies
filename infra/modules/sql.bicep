// Azure SQL logical server with Microsoft Entra-only authentication (no SQL login, no password)
// and the application database with its backup policies.

@description('Azure region.')
param location string

@description('Tags applied to every resource.')
param tags object

@description('Logical server name (globally unique, lowercase).')
@maxLength(63)
param serverName string

@description('Database name.')
param databaseName string

@description('Object id of the Microsoft Entra group that administers the server.')
param adminGroupObjectId string

@description('Display name of that Microsoft Entra group (shown as the server admin login).')
param adminGroupName string

@description('Database SKU. Free = Azure SQL free offer (serverless General Purpose, auto-pause when the monthly free amount is used).')
@allowed([
  'Free'
  'Basic'
  'S0'
  'S1'
  'S2'
])
param skuName string

@description('Backup storage redundancy: Geo (production, restorable in France South) or Local (recette).')
@allowed([
  'Local'
  'Zone'
  'Geo'
])
param backupStorageRedundancy string

@description('Point-in-time restore window in days (Basic and Free: 1 to 7; Standard: up to 35).')
@minValue(1)
@maxValue(35)
param shortTermRetentionDays int

@description('Enable long-term retention (weekly and monthly full backups kept in Azure storage).')
param longTermRetentionEnabled bool

@description('Weekly LTR retention (ISO 8601 duration, PT0S disables).')
param ltrWeeklyRetention string

@description('Monthly LTR retention (ISO 8601 duration, PT0S disables).')
param ltrMonthlyRetention string

@description('Yearly LTR retention (ISO 8601 duration, PT0S disables).')
param ltrYearlyRetention string

@description('Week of the year whose backup is kept as the yearly backup (used only when yearly retention is enabled).')
@minValue(1)
@maxValue(52)
param ltrWeekOfYear int

var skus = {
  Free: {
    sku: {
      name: 'GP_S_Gen5'
      tier: 'GeneralPurpose'
      family: 'Gen5'
      capacity: 2
    }
    maxSizeBytes: 34359738368 // 32 GB, the free offer limit
  }
  Basic: {
    sku: {
      name: 'Basic'
      tier: 'Basic'
      capacity: 5
    }
    maxSizeBytes: 2147483648 // 2 GB, the Basic maximum
  }
  S0: {
    sku: {
      name: 'S0'
      tier: 'Standard'
      capacity: 10
    }
    maxSizeBytes: 268435456000 // 250 GB, included in the Standard price
  }
  S1: {
    sku: {
      name: 'S1'
      tier: 'Standard'
      capacity: 20
    }
    maxSizeBytes: 268435456000
  }
  S2: {
    sku: {
      name: 'S2'
      tier: 'Standard'
      capacity: 50
    }
    maxSizeBytes: 268435456000
  }
}

var isFreeOffer = skuName == 'Free'

resource sqlServer 'Microsoft.Sql/servers@2025-01-01' = {
  name: serverName
  location: location
  tags: tags
  properties: {
    version: '12.0'
    minimalTlsVersion: '1.2'
    publicNetworkAccess: 'Enabled'
    restrictOutboundNetworkAccess: 'Disabled'
    // Entra-only authentication from creation: no administratorLogin / password at all.
    // This block is applied at creation; change the admin later with `az sql server ad-admin update`.
    administrators: {
      administratorType: 'ActiveDirectory'
      azureADOnlyAuthentication: true
      principalType: 'Group'
      login: adminGroupName
      sid: adminGroupObjectId
      tenantId: tenant().tenantId
    }
  }
}

// "Allow Azure services and resources to access this server": needed by App Service without
// VNet integration. Authentication remains Entra-only (see infra/README.md for the trade-off).
resource allowAzureServices 'Microsoft.Sql/servers/firewallRules@2025-01-01' = {
  parent: sqlServer
  name: 'AllowAllWindowsAzureIps'
  properties: {
    startIpAddress: '0.0.0.0'
    endIpAddress: '0.0.0.0'
  }
}

resource database 'Microsoft.Sql/servers/databases@2025-01-01' = {
  parent: sqlServer
  name: databaseName
  location: location
  tags: tags
  sku: skus[skuName].sku
  properties: {
    collation: 'SQL_Latin1_General_CP1_CI_AS'
    maxSizeBytes: skus[skuName].maxSizeBytes
    zoneRedundant: false
    readScale: 'Disabled'
    requestedBackupStorageRedundancy: backupStorageRedundancy
    // Free offer only (ignored otherwise).
    useFreeLimit: isFreeOffer ? true : null
    freeLimitExhaustionBehavior: isFreeOffer ? 'AutoPause' : null
    autoPauseDelay: isFreeOffer ? 60 : null
    minCapacity: isFreeOffer ? json('0.5') : null
  }
}

resource shortTermRetention 'Microsoft.Sql/servers/databases/backupShortTermRetentionPolicies@2025-01-01' = {
  parent: database
  name: 'default'
  properties: {
    retentionDays: shortTermRetentionDays
  }
}

resource longTermRetention 'Microsoft.Sql/servers/databases/backupLongTermRetentionPolicies@2025-01-01' = if (longTermRetentionEnabled) {
  parent: database
  name: 'default'
  properties: {
    weeklyRetention: ltrWeeklyRetention
    monthlyRetention: ltrMonthlyRetention
    yearlyRetention: ltrYearlyRetention
    weekOfYear: ltrYearlyRetention == 'PT0S' ? 0 : ltrWeekOfYear
  }
  // Two backup policy updates on the same database at once fail with a conflict.
  dependsOn: [
    shortTermRetention
  ]
}

output serverName string = sqlServer.name
output serverFqdn string = sqlServer.properties.fullyQualifiedDomainName
output databaseName string = database.name
output databaseId string = database.id
