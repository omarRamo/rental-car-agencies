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

@description('E-mail of the first support account (the platform operator, ADR-0067), created at startup without password when no support account exists (Identity__InitialSupportEmail). The support sets the password with "forgot password", then registers the agencies and invites their managers.')
param initialSupportEmail string = ''

@description('Custom domains accepted by the host filter, separated by semicolons (HostFiltering__CustomDomains), e.g. "www.example.tn;example.tn". *.azurewebsites.net is always accepted.')
param customDomains string = ''

@description('Public base URL of the site (PublicSite__BaseUrl). Empty = the default azurewebsites.net host name.')
param publicSiteBaseUrl string = ''

@description('Agency name shown on the public site (PublicSite__AgencyName).')
param publicSiteAgencyName string = 'CarRental'

@description('WhatsApp number of the agency in international format without + (PublicSite__WhatsAppNumber). Empty = WhatsApp button hidden.')
param publicSiteWhatsAppNumber string = ''

// ---------------------------------------------------------------------------------------------
// Origin reserved to Cloudflare (ADR-0062)
// ---------------------------------------------------------------------------------------------

@description('Reserve the main site to the Cloudflare IP ranges below (App Service access restrictions, everything else gets 403) and trust CF-Connecting-IP (ReverseProxy__TrustCloudflareHeader). Turn on once the custom domain is proxied by Cloudflare; the SCM site keeps its own open rules for the deployment, and the App Service health check is internal (never blocked). Set HEALTHCHECK_BASE_URL in GitHub then (infra/README.md, section 8).')
param restrictToCloudflare bool = false

// Source: https://www.cloudflare.com/ips-v4 and https://www.cloudflare.com/ips-v6 (also https://api.cloudflare.com/client/v4/ips),
// read on 2026-10-21. Cloudflare announces changes in advance; compare once a quarter and redeploy (docs/runbook.md, "Cloudflare").
@description('IP ranges (CIDR) of Cloudflare allowed on the main site when restrictToCloudflare is true.')
param cloudflareIpRanges array = [
  '173.245.48.0/20'
  '103.21.244.0/22'
  '103.22.200.0/22'
  '103.31.4.0/22'
  '141.101.64.0/18'
  '108.162.192.0/18'
  '190.93.240.0/20'
  '188.114.96.0/20'
  '197.234.240.0/22'
  '198.41.128.0/17'
  '162.158.0.0/15'
  '104.16.0.0/13'
  '104.24.0.0/14'
  '172.64.0.0/13'
  '131.0.72.0/22'
  '2400:cb00::/32'
  '2606:4700::/32'
  '2803:f800::/32'
  '2405:b500::/32'
  '2405:8100::/32'
  '2a06:98c0::/29'
  '2c0f:f248::/32'
]

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

@description('LTR: keep one backup per year for this ISO 8601 duration (PT0S = disabled; P10Y = 10 years, the accounting retention to confirm with the accountant, ADR-0061).')
param sqlLtrYearlyRetention string = 'PT0S'

@description('LTR: week of the year whose backup becomes the yearly backup (2 = the second week of January: the closed year is complete, whatever the calendar).')
@minValue(1)
@maxValue(52)
param sqlLtrWeekOfYear int = 2

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

@description('Point-in-time restore window for blobs, in days (0 = disabled; must stay below blobSoftDeleteDays). Incompatible with immutable storage: ignored when invoiceImmutabilityDays is set (ADR-0061).')
@minValue(0)
@maxValue(364)
param blobPointInTimeRestoreDays int = 0

@description('Delete previous blob versions older than this many days (0 = keep forever).')
@minValue(0)
param blobVersionRetentionDays int = 90

@description('Time-based retention (WORM) of the invoices container, in days: invoices and credit notes can be added but neither overwritten nor deleted during this period (0 = none; 3650 = 10 years, to confirm with the accountant). The policy stays unlocked: only an administrator can shorten or remove it, never the application (ADR-0061).')
@minValue(0)
@maxValue(146000)
param invoiceImmutabilityDays int = 0

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

@description('E-mail addresses notified by the alert rules (empty = no action group and no alert rule). See infra/README.md, "Alertes".')
param alertEmails array = []

@description('Optional SMS receivers of the alerts, e.g. [{ countryCode: \'216\', phoneNumber: \'20123456\' }] (billed from the first SMS outside the United States).')
param alertSmsNumbers array = []

@description('Public URL of /health watched by an Application Insights standard availability test, billed per execution (empty = none; a free external monitor can do it, infra/README.md).')
param availabilityTestUrl string = ''

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
  actionGroup: 'ag-${baseName}-${environmentName}'
  alertPrefix: 'alert-${baseName}-${environmentName}'
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
  'invoices' // invoices and credit notes (PDF, TEIF XML)
  'exports' // accounting and Excel exports
  'fleet' // category photos (served anonymously by the app) and vehicle photos
  dataProtectionContainerName // ASP.NET Core Data Protection key ring
]

var aspNetCoreEnvironment = environmentName == 'prod' ? 'Production' : 'Staging'

// The Free plan has neither Always On nor a useful health check (a probe every minute would also keep the free
// serverless database awake and use up its monthly allowance).
var isFreePlan = appServicePlanSku == 'F1'

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
    immutableContainers: invoiceImmutabilityDays > 0
      ? [
          {
            name: 'invoices'
            days: invoiceImmutabilityDays
          }
        ]
      : []
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
    restrictToCloudflare: restrictToCloudflare
    cloudflareIpRanges: cloudflareIpRanges
    // Configuration contract with the application (double underscore = configuration section).
    // No secret here: secrets live in Key Vault (configuration provider on KeyVault__Uri).
    appSettings: {
      ASPNETCORE_ENVIRONMENT: aspNetCoreEnvironment
      ASPNETCORE_FORWARDEDHEADERS_ENABLED: 'true'
      // CF-Connecting-IP can only be trusted when nothing but Cloudflare reaches the origin (ADR-0062).
      ReverseProxy__TrustCloudflareHeader: restrictToCloudflare ? 'true' : 'false'
      SCM_DO_BUILD_DURING_DEPLOYMENT: 'false'
      ConnectionStrings__Sql: 'Server=tcp:${sql.outputs.serverFqdn},1433;Database=${sql.outputs.databaseName};Authentication=Active Directory Default;Encrypt=True;'
      Storage__BlobServiceUri: storage.outputs.blobEndpoint
      DataProtection__BlobUri: '${storage.outputs.blobEndpoint}${dataProtectionContainerName}/keys.xml'
      DataProtection__KeyVaultKeyUri: keyVault.outputs.dataProtectionKeyUri
      KeyVault__Uri: keyVault.outputs.uri
      Email__AcsEndpoint: communication.outputs.endpoint
      Email__SenderAddress: communication.outputs.senderAddress
      Seed__Demo: seedDemoData ? 'true' : 'false'
      Identity__InitialSupportEmail: initialSupportEmail
      HostFiltering__CustomDomains: customDomains
      PublicSite__BaseUrl: empty(publicSiteBaseUrl) ? 'https://${names.webApp}.azurewebsites.net' : publicSiteBaseUrl
      PublicSite__AgencyName: publicSiteAgencyName
      PublicSite__WhatsAppNumber: publicSiteWhatsAppNumber
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

// Alert rules and their action group (ADR-0060), only when someone receives them.
module alerts 'modules/alerts.bicep' = if (!empty(alertEmails)) {
  name: 'alerts'
  params: {
    location: location
    tags: allTags
    namePrefix: names.alertPrefix
    actionGroupName: names.actionGroup
    actionGroupShortName: take('${baseName}${envShort}', 12)
    alertEmails: alertEmails
    alertSmsNumbers: alertSmsNumbers
    webAppId: web.outputs.id
    appInsightsId: monitoring.outputs.appInsightsId
    sqlDatabaseId: sql.outputs.databaseId
    healthCheckEnabled: !isFreePlan
    alwaysOn: !isFreePlan
    sqlDtuBased: sqlDatabaseSku != 'Free'
    availabilityTestUrl: availabilityTestUrl
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
output alertRuleNames array = empty(alertEmails) ? [] : alerts!.outputs.alertNames
