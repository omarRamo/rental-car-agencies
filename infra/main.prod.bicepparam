// Production — App Service B1, Azure SQL Basic (geo-redundant backups + long-term retention),
// geo-redundant storage with point-in-time restore, Key Vault with purge protection.
//
// Tenant-specific values are read from environment variables at deployment time, so that no
// identifier or e-mail address is committed (see infra/README.md, "Premier déploiement"):
//   SQL_ADMIN_GROUP_OBJECT_ID  object id of the Microsoft Entra group "CarRental SQL Admins"
//   SQL_ADMIN_GROUP_NAME       display name of that group
//   BUDGET_ALERT_EMAIL         optional: address alerted by the monthly budget
using './main.bicep'

param environmentName = 'prod'

// App Service
param appServicePlanSku = 'B1'
param seedDemoData = false

// Azure SQL (upgrade to S0: set 'S0', redeploy — see README)
param sqlAdminGroupObjectId = readEnvironmentVariable('SQL_ADMIN_GROUP_OBJECT_ID')
param sqlAdminGroupName = readEnvironmentVariable('SQL_ADMIN_GROUP_NAME')
param sqlDatabaseSku = 'Basic'
param sqlBackupStorageRedundancy = 'Geo'
param sqlShortTermRetentionDays = 7
param sqlLongTermRetentionEnabled = true
param sqlLtrWeeklyRetention = 'P5W'
param sqlLtrMonthlyRetention = 'P12M'
param sqlLtrYearlyRetention = 'PT0S'

// Storage
param storageSkuName = 'Standard_GRS'
param blobSoftDeleteDays = 30
param containerSoftDeleteDays = 30
param blobPointInTimeRestoreDays = 14
param blobVersionRetentionDays = 90

// Key Vault
param keyVaultPurgeProtection = true
param keyVaultSoftDeleteRetentionDays = 90

// Monitoring: about 4.7 GB per month at most; the first 5 GB per billing account and month are free.
param logDailyCapGb = '0.15'
param logRetentionDays = 30

// E-mail
param communicationDataLocation = 'France'
param emailSenderDisplayName = 'CarRental'
param emailCustomDomain = ''
param emailCustomDomainLinked = false

// Budget (billing currency of the subscription)
param budgetAmount = 40
param budgetContactEmails = empty(readEnvironmentVariable('BUDGET_ALERT_EMAIL', ''))
  ? []
  : [
      readEnvironmentVariable('BUDGET_ALERT_EMAIL', '')
    ]
