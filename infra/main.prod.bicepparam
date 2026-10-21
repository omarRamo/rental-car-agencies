// Production — App Service B1, Azure SQL Basic (geo-redundant backups + long-term retention, yearly backup kept
// 10 years), geo-redundant storage with immutable invoices, Key Vault with purge protection, alert rules.
//
// Tenant-specific values are read from environment variables at deployment time, so that no
// identifier or e-mail address is committed (see infra/README.md, "Premier déploiement"):
//   SQL_ADMIN_GROUP_OBJECT_ID  object id of the Microsoft Entra group "CarRental SQL Admins"
//   SQL_ADMIN_GROUP_NAME       display name of that group
//   BUDGET_ALERT_EMAIL         optional: address alerted by the monthly budget
//   ALERT_EMAILS               optional: addresses alerted by the alert rules, separated by ";" (no alert rule when empty)
using './main.bicep'

param environmentName = 'prod'

// App Service
param appServicePlanSku = 'B1'
param seedDemoData = false

// Origin reserved to Cloudflare (ADR-0062): set to true once www.<domain> is proxied by Cloudflare (orange cloud),
// together with the GitHub variable HEALTHCHECK_BASE_URL (infra/README.md, section 8). Off until then, otherwise the
// azurewebsites.net address answers 403 to everyone.
param restrictToCloudflare = false

// Azure SQL (upgrade to S0: set 'S0', redeploy — see README)
param sqlAdminGroupObjectId = readEnvironmentVariable('SQL_ADMIN_GROUP_OBJECT_ID')
param sqlAdminGroupName = readEnvironmentVariable('SQL_ADMIN_GROUP_NAME')
param sqlDatabaseSku = 'Basic'
param sqlBackupStorageRedundancy = 'Geo'
param sqlShortTermRetentionDays = 7
param sqlLongTermRetentionEnabled = true
param sqlLtrWeeklyRetention = 'P5W'
param sqlLtrMonthlyRetention = 'P12M'
// Accounting documents: 10 years (Tunisian bookkeeping rules, to confirm with the accountant — ADR-0061).
param sqlLtrYearlyRetention = 'P10Y'
param sqlLtrWeekOfYear = 2

// Storage. Point-in-time restore is incompatible with the immutable invoices container: versions (90 days) and
// soft delete (30 days) remain the means of restoring a blob (docs/runbook.md, "Restaurations").
param storageSkuName = 'Standard_GRS'
param blobSoftDeleteDays = 30
param containerSoftDeleteDays = 30
param blobPointInTimeRestoreDays = 0
param blobVersionRetentionDays = 90
// Invoices and credit notes: WORM 10 years (unlocked policy; to confirm with the accountant — ADR-0061).
param invoiceImmutabilityDays = 3650

// Key Vault
param keyVaultPurgeProtection = true
param keyVaultSoftDeleteRetentionDays = 90

// Monitoring: about 4.7 GB per month at most; the first 5 GB per billing account and month are free.
param logDailyCapGb = '0.15'
param logRetentionDays = 30

// Alerts (ADR-0060). Optional standard availability test of the public site, billed per execution: leave empty and use
// a free external monitor, or set 'https://www.<domain>/health' (infra/README.md, "Alertes").
param alertEmails = empty(readEnvironmentVariable('ALERT_EMAILS', ''))
  ? []
  : split(readEnvironmentVariable('ALERT_EMAILS', ''), ';')
param alertSmsNumbers = []
param availabilityTestUrl = ''

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
