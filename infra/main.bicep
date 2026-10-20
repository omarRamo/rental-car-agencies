// Car rental platform — Azure infrastructure for one environment (one resource group each).
// Deploy:   az deployment group create -g <rg> --parameters infra/main.<env>.bicepparam
// Details:  infra/README.md (architecture, costs, first deployment, operations).

targetScope = 'resourceGroup'

metadata description = 'Car rental platform: App Service (Linux), Azure SQL (Entra-only), Blob Storage, Key Vault, Application Insights, Communication Services Email.'

// ---------------------------------------------------------------------------------------------
// General
// ---------------------------------------------------------------------------------------------

@description('Environment. "recette" = staging/acceptance (ASPNETCORE_ENVIRONMENT=Staging), "prod" = production.')
@allowed([
  'recette'
  'prod'
])
param environmentName string

@description('Base name for resource names: lowercase letters and digits only, 3 to 10 characters.')
@minLength(3)
@maxLength(10)
param baseName string = 'carrental'

@description('Azure region of the regional resources.')
param location string = 'francecentral'

@description('Extra tags merged into the default tags.')
param tags object = {}

// ---------------------------------------------------------------------------------------------
// App Service
// ---------------------------------------------------------------------------------------------

@description('App Service plan SKU: B1 in production (Always On), F1 in recette (free, no Always On, no custom domain).')
@allowed([
  'F1'
  'B1'
  'B2'
  'B3'
  'S1'
  'P0v3'
  'P1v3'
])
param appServicePlanSku string = 'B1'

@description('Load the demo data set at startup (Seed__Demo). Recette only.')
param seedDemoData bool = false

// ---------------------------------------------------------------------------------------------
// Azure SQL
// ---------------------------------------------------------------------------------------------

@description('Object id of the Microsoft Entra group administering Azure SQL (members can create the database users).')
@minLength(36)
@maxLength(36)
param sqlAdminGroupObjectId string

@description('Display name of that Microsoft Entra group.')
@minLength(1)
param sqlAdminGroupName string

@description('Database SKU: Basic (5 DTU, 2 GB) by default, S0 (10 DTU) when needed, Free = Azure SQL free offer (recette).')
@allowed([
  'Free'
  'Basic'
  'S0'
  'S1'
  'S2'
])
param sqlDatabaseSku string = 'Basic'

@description('Backup storage redundancy: Geo (geo-restore possible) or Local (cheapest).')
@allowed([
  'Local'
  'Zone'
  'Geo'
])
param sqlBackupStorageRedundancy string = 'Geo'

@description('Point-in-time restore window in days (7 is the Basic maximum).')
@minValue(1)
@maxValue(35)
param sqlShortTermRetentionDays int = 7

@description('Long-term retention of full backups (weekly and monthly).')
param sqlLongTermRetentionEnabled bool = true

@description('LTR: keep each weekly backup for this ISO 8601 duration.')
param sqlLtrWeeklyRetention string = 'P5W'

@description('LTR: keep the first backup of each month for this ISO 8601 duration.')
param sqlLtrMonthlyRetention string = 'P12M'

@description('LTR: keep one backup per year for this ISO 8601 duration (PT0S = disabled; for example P10Y once agreed with the accountant).')
param sqlLtrYearlyRetention string = 'PT0S'

@description('LTR: week of the year whose backup becomes the yearly backup.')
@minValue(1)
@maxValue(52)
param sqlLtrWeekOfYear int = 1

// ---------------------------------------------------------------------------------------------
// Storage
// ---------------------------------------------------------------------------------------------

@description('Storage replication.')
@allowed([
  'Standard_LRS'
  'Standard_ZRS'
  'Standard_GRS'
  'Standard_GZRS'
])
param storageSkuName string = 'Standard_LRS'

@description('Blob soft delete retention in days.')
@minValue(1)
@maxValue(365)
param blobSoftDeleteDays int = 30

@description('Container soft delete retention in days.')
@minValue(1)
@maxValue(365)
param containerSoftDeleteDays int = 30

@description('Point-in-time restore window for blobs, in days (0 = disabled; must stay below blobSoftDeleteDays).')
@minValue(0)
@maxValue(364)
param blobPointInTimeRestoreDays int = 0

@description('Delete previous blob versions older than this many days (0 = keep forever).')
@minValue(0)
param blobVersionRetentionDays int = 90

// ---------------------------------------------------------------------------------------------
// Key Vault
// ---------------------------------------------------------------------------------------------

@description('Purge protection on the Key Vault (irreversible).')
param keyVaultPurgeProtection bool = true

@description('Key Vault soft delete retention in days (fixed at creation).')
@minValue(7)
@maxValue(90)
param keyVaultSoftDeleteRetentionDays int = 90

@description('Grant the SQL administrators group "Key Vault Secrets Officer" to manage application secrets.')
param grantAdminGroupKeyVaultSecretsOfficer bool = true

// ---------------------------------------------------------------------------------------------
// Monitoring
// ---------------------------------------------------------------------------------------------

@description('Daily ingestion cap (GB) of the Log Analytics workspace, which also stores the Application Insights telemetry, as a decimal string.')
param logDailyCapGb string = '0.15'

@description('Log Analytics retention in days.')
@minValue(30)
@maxValue(730)
param logRetentionDays int = 30

// ---------------------------------------------------------------------------------------------
// E-mail (Azure Communication Services)
// ---------------------------------------------------------------------------------------------

@description('Data location of the Communication Services resources (France keeps data at rest in France; Europe is the fallback).')
param communicationDataLocation string = 'France'

@description('Sender display name of the e-mails.')
param emailSenderDisplayName string = 'CarRental'

@description('Optional custom e-mail domain (for example agence.tn), added unverified; see infra/README.md.')
param emailCustomDomain string = ''

@description('Set to true once the custom domain DNS records are verified: links it and uses noreply@<domain> as sender.')
param emailCustomDomainLinked bool = false

// ---------------------------------------------------------------------------------------------
// Budget (optional)
// ---------------------------------------------------------------------------------------------

@description('Monthly budget of the resource group, in the billing currency (0 = no budget).')
@minValue(0)
param budgetAmount int = 0

@description('E-mail addresses alerted by the budget (no budget when empty).')
param budgetContactEmails array = []

@description('Budget start: first day of the current month. Leave the default.')
param budgetStartDate string = utcNow('yyyy-MM-01')

// ---------------------------------------------------------------------------------------------
// Names and shared values
// ---------------------------------------------------------------------------------------------

var suffix = take(uniqueString(resourceGroup().id, baseName, environmentName), 6)
var envShort = environmentName == 'prod' ? 'prd' : 'rec'

var names = {
  plan: 'asp-${baseName}-${environmentName}'
  webApp: 'app-${baseName}-${environmentName}-${suffix}'
  sqlServer: 'sql-${baseName}-${environmentName}-${suffix}'
  database: baseName
  storage: 'st${baseName}${envShort}${suffix}'
  keyVault: 'kv-${baseName}-${envShort}-${suffix}'
  logAnalytics: 'log-${baseName}-${environmentName}'
  appInsights: 'appi-${baseName}-${environmentName}'
  emailService: 'ecs-${baseName}-${environmentName}-${suffix}'
  communication: 'acs-${baseName}-${environmentName}-${suffix}'
  budget: 'budget-${baseName}-${environmentName}'
}

var defaultTags = {
  application: 'car-rental'
  environment: environmentName
  'managed-by': 'bicep'
  repository: 'omarRamo/rental-car-agencies'
}
var allTags = union(defaultTags, tags)

var dataProtectionContainerName = 'dataprotection'
var containerNames = [
  'documents' // identity documents and driving licences
  'inspections' // vehicle check-in / check-out photos
  'contracts' // rental contract PDFs
  'exports' // accounting and Excel exports
  dataProtectionContainerName // ASP.NET Core Data Protection key ring
]

var aspNetCoreEnvironment = environmentName == 'prod' ? 'Production' : 'Staging'

// ---------------------------------------------------------------------------------------------
// Modules
// ---------------------------------------------------------------------------------------------

module monitoring 'modules/monitoring.bicep' = {
  name: 'monitoring'
  params: {
    location: location
    tags: allTags
    workspaceName: names.logAnalytics
    appInsightsName: names.appInsights
    dailyCapGb: logDailyCapGb
    retentionInDays: logRetentionDays
  }
}

module storage 'modules/storage.bicep' = {
  name: 'storage'
  params: {
    location: location
    tags: allTags
    storageAccountName: names.storage
    skuName: storageSkuName
    containerNames: containerNames
    blobSoftDeleteDays: blobSoftDeleteDays
    containerSoftDeleteDays: containerSoftDeleteDays
    pointInTimeRestoreDays: blobPointInTimeRestoreDays
    versionRetentionDays: blobVersionRetentionDays
  }
}

module keyVault 'modules/keyvault.bicep' = {
  name: 'keyvault'
  params: {
    location: location
    tags: allTags
    keyVaultName: names.keyVault
    softDeleteRetentionInDays: keyVaultSoftDeleteRetentionDays
    enablePurgeProtection: keyVaultPurgeProtection
    dataProtectionKeyName: 'dataprotection'
    logAnalyticsWorkspaceId: monitoring.outputs.workspaceId
  }
}

module sql 'modules/sql.bicep' = {
  name: 'sql'
  params: {
    location: location
    tags: allTags
    serverName: names.sqlServer
    databaseName: names.database
    adminGroupObjectId: sqlAdminGroupObjectId
    adminGroupName: sqlAdminGroupName
    skuName: sqlDatabaseSku
    backupStorageRedundancy: sqlBackupStorageRedundancy
    shortTermRetentionDays: sqlShortTermRetentionDays
    longTermRetentionEnabled: sqlLongTermRetentionEnabled
    ltrWeeklyRetention: sqlLtrWeeklyRetention
    ltrMonthlyRetention: sqlLtrMonthlyRetention
    ltrYearlyRetention: sqlLtrYearlyRetention
    ltrWeekOfYear: sqlLtrWeekOfYear
  }
}

module communication 'modules/communication.bicep' = {
  name: 'communication'
  params: {
    tags: allTags
    emailServiceName: names.emailService
    communicationServiceName: names.communication
    dataLocation: communicationDataLocation
    senderDisplayName: emailSenderDisplayName
    customDomainName: emailCustomDomain
    linkCustomDomain: emailCustomDomainLinked
  }
}

module web 'modules/appservice.bicep' = {
  name: 'appservice'
  params: {
    location: location
    tags: allTags
    planName: names.plan
    webAppName: names.webApp
    skuName: appServicePlanSku
    appInsightsName: monitoring.outputs.appInsightsName
    healthCheckPath: '/health/ready'
    startupCommand: 'dotnet CarRental.Web.dll'
    // Configuration contract with the application (double underscore = configuration section).
    // No secret here: secrets live in Key Vault (configuration provider on KeyVault__Uri).
    appSettings: {
      ASPNETCORE_ENVIRONMENT: aspNetCoreEnvironment
      ASPNETCORE_FORWARDEDHEADERS_ENABLED: 'true'
      SCM_DO_BUILD_DURING_DEPLOYMENT: 'false'
      ConnectionStrings__Sql: 'Server=tcp:${sql.outputs.serverFqdn},1433;Database=${sql.outputs.databaseName};Authentication=Active Directory Default;Encrypt=True;'
      Storage__BlobServiceUri: storage.outputs.blobEndpoint
      DataProtection__BlobUri: '${storage.outputs.blobEndpoint}${dataProtectionContainerName}/keys.xml'
      DataProtection__KeyVaultKeyUri: keyVault.outputs.dataProtectionKeyUri
      KeyVault__Uri: keyVault.outputs.uri
      Email__AcsEndpoint: communication.outputs.endpoint
      Email__SenderAddress: communication.outputs.senderAddress
      Seed__Demo: seedDemoData ? 'true' : 'false'
    }
  }
}

module rbac 'modules/rbac.bicep' = {
  name: 'rbac'
  params: {
    webAppPrincipalId: web.outputs.principalId
    storageAccountName: storage.outputs.name
    keyVaultName: keyVault.outputs.name
    communicationServiceName: communication.outputs.name
    adminGroupObjectId: sqlAdminGroupObjectId
    grantAdminGroupKeyVaultSecretsOfficer: grantAdminGroupKeyVaultSecretsOfficer
  }
}

module budget 'modules/budget.bicep' = if (budgetAmount > 0 && !empty(budgetContactEmails)) {
  name: 'budget'
  params: {
    budgetName: names.budget
    amount: max(budgetAmount, 1) // budgetAmount > 0 here; max() only narrows the type
    contactEmails: budgetContactEmails
    startDate: budgetStartDate
  }
}

// ---------------------------------------------------------------------------------------------
// Outputs (copy them into the GitHub environment variables, see infra/README.md)
// ---------------------------------------------------------------------------------------------

output webAppName string = web.outputs.name
output webAppHostName string = web.outputs.defaultHostName
output webAppPrincipalId string = web.outputs.principalId
output customDomainVerificationId string = web.outputs.customDomainVerificationId
output sqlServerName string = sql.outputs.serverName
output sqlServerFqdn string = sql.outputs.serverFqdn
output sqlDatabaseName string = sql.outputs.databaseName
output storageAccountName string = storage.outputs.name
output storageBlobEndpoint string = storage.outputs.blobEndpoint
output keyVaultName string = keyVault.outputs.name
output keyVaultUri string = keyVault.outputs.uri
output communicationServiceName string = communication.outputs.name
output emailSenderAddress string = communication.outputs.senderAddress
