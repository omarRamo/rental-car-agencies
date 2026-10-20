// Recette (acceptance) — same template as production, free or cheapest tiers:
// App Service F1, Azure SQL free offer, locally redundant storage, Key Vault without purge
// protection (the environment can be deleted and recreated), demo data loaded at startup.
//
// Tenant-specific values are read from environment variables at deployment time:
//   SQL_ADMIN_GROUP_OBJECT_ID  object id of the Microsoft Entra group "CarRental SQL Admins"
//   SQL_ADMIN_GROUP_NAME       display name of that group
//   BUDGET_ALERT_EMAIL         optional: address alerted by the monthly budget
using './main.bicep'

param environmentName = 'recette'

// App Service: free tier (no Always On, no custom domain, 60 CPU minutes per day).
param appServicePlanSku = 'F1'
param seedDemoData = true

// Azure SQL free offer. If the subscription has already used it, switch to 'Basic' (about 5 USD/month).
param sqlAdminGroupObjectId = readEnvironmentVariable('SQL_ADMIN_GROUP_OBJECT_ID')
param sqlAdminGroupName = readEnvironmentVariable('SQL_ADMIN_GROUP_NAME')
param sqlDatabaseSku = 'Free'
param sqlBackupStorageRedundancy = 'Local'
param sqlShortTermRetentionDays = 7
param sqlLongTermRetentionEnabled = false

// Storage
param storageSkuName = 'Standard_LRS'
param blobSoftDeleteDays = 7
param containerSoftDeleteDays = 7
param blobPointInTimeRestoreDays = 0
param blobVersionRetentionDays = 30

// Key Vault
param keyVaultPurgeProtection = false
param keyVaultSoftDeleteRetentionDays = 7

// Monitoring
param logDailyCapGb = '0.1'
param logRetentionDays = 30

// E-mail
param communicationDataLocation = 'France'
param emailSenderDisplayName = 'CarRental (recette)'
param emailCustomDomain = ''
param emailCustomDomainLinked = false

// Budget: alerts if a recette resource is accidentally scaled up.
param budgetAmount = 10
param budgetContactEmails = empty(readEnvironmentVariable('BUDGET_ALERT_EMAIL', ''))
  ? []
  : [
      readEnvironmentVariable('BUDGET_ALERT_EMAIL', '')
    ]
