// Recette (acceptance) — same template as production, free or cheapest tiers:
// App Service F1, Azure SQL free offer, locally redundant storage, Key Vault without purge
// protection (the environment can be deleted and recreated), demo data loaded at startup.
// No long-term retention, no immutability, no alert rule by default (ALERT_EMAILS unset).
//
// Tenant-specific values are read from environment variables at deployment time:
//   SQL_ADMIN_GROUP_OBJECT_ID  object id of the Microsoft Entra group "CarRental SQL Admins"
//   SQL_ADMIN_GROUP_NAME       display name of that group
//   BUDGET_ALERT_EMAIL         optional: address alerted by the monthly budget
//   ALERT_EMAILS               optional: addresses alerted by the alert rules, separated by ";"
using './main.bicep'

param environmentName = 'recette'

// App Service: free tier (no Always On, no custom domain, 60 CPU minutes per day).
param appServicePlanSku = 'F1'
param seedDemoData = true

// The recette stays on *.azurewebsites.net, without Cloudflare: the origin is not restricted.
param restrictToCloudflare = false

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
param invoiceImmutabilityDays = 0

// Key Vault
param keyVaultPurgeProtection = false
param keyVaultSoftDeleteRetentionDays = 7

// Monitoring
param logDailyCapGb = '0.1'
param logRetentionDays = 30

// Alerts: only if ALERT_EMAILS is set (the F1 plan has no health check and sleeps when idle: fewer rules).
param alertEmails = empty(readEnvironmentVariable('ALERT_EMAILS', ''))
  ? []
  : split(readEnvironmentVariable('ALERT_EMAILS', ''), ';')
param alertSmsNumbers = []
param availabilityTestUrl = ''

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
