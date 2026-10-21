// Linux App Service plan + the single Blazor Web App (system-assigned managed identity).

@description('Azure region.')
param location string

@description('Tags applied to every resource.')
param tags object

@description('App Service plan name.')
param planName string

@description('Web App name (globally unique: <name>.azurewebsites.net).')
@maxLength(60)
param webAppName string

@description('Plan SKU. F1 has no Always On and no custom domain; B1 is the production plan.')
@allowed([
  'F1'
  'B1'
  'B2'
  'B3'
  'S1'
  'P0v3'
  'P1v3'
])
param skuName string

@description('Application settings (environment variables), as a name -> value object. Secrets never go here: use Key Vault.')
param appSettings object

@description('Application Insights component whose connection string is injected as APPLICATIONINSIGHTS_CONNECTION_STRING.')
param appInsightsName string

@description('Path probed by the App Service health check (ignored on F1).')
param healthCheckPath string

@description('Startup command. The host project is src/CarRental.Web, published as CarRental.Web.dll.')
param startupCommand string

@description('Reserve the main site to cloudflareIpRanges (403 for any other address). The SCM site keeps its own rules (open: the deployment authenticates with Microsoft Entra), and the App Service health check is internal.')
param restrictToCloudflare bool

@description('IP ranges (CIDR, IPv4 and IPv6) allowed on the main site when restrictToCloudflare is true.')
param cloudflareIpRanges array

var skuTiers = {
  F1: 'Free'
  B1: 'Basic'
  B2: 'Basic'
  B3: 'Basic'
  S1: 'Standard'
  P0v3: 'Premium0V3'
  P1v3: 'PremiumV3'
}

// Always On and the health check are not available on the Free tier.
var isFreeTier = skuName == 'F1'

var appSettingsList = [
  for setting in items(appSettings): {
    name: setting.key
    value: setting.value
  }
]

// One allow rule per Cloudflare range; the implicit "deny all" of App Service applies after the last rule.
var cloudflareRules = [
  for (range, i) in cloudflareIpRanges: {
    name: 'cloudflare-${i + 1}'
    description: 'Cloudflare ${range}'
    ipAddress: range
    action: 'Allow'
    tag: 'Default'
    priority: 100 + i
  }
]

resource appInsights 'Microsoft.Insights/components@2020-02-02' existing = {
  name: appInsightsName
}

resource plan 'Microsoft.Web/serverfarms@2025-03-01' = {
  name: planName
  location: location
  tags: tags
  kind: 'linux'
  sku: {
    name: skuName
    tier: skuTiers[skuName]
    capacity: isFreeTier ? null : 1 // one instance (Blazor Server circuits, no shared cache)
  }
  properties: {
    reserved: true // Linux
    zoneRedundant: false
  }
}

resource webApp 'Microsoft.Web/sites@2025-03-01' = {
  name: webAppName
  location: location
  tags: tags
  kind: 'app,linux'
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    // ARR affinity: Blazor Server circuits must stay on the instance that created them.
    clientAffinityEnabled: true
    publicNetworkAccess: 'Enabled'
    keyVaultReferenceIdentity: 'SystemAssigned'
    siteConfig: {
      linuxFxVersion: 'DOTNETCORE|10.0'
      appCommandLine: startupCommand
      alwaysOn: !isFreeTier
      webSocketsEnabled: true
      http20Enabled: true
      minTlsVersion: '1.2'
      scmMinTlsVersion: '1.2'
      ftpsState: 'Disabled'
      remoteDebuggingEnabled: false
      healthCheckPath: isFreeTier ? null : healthCheckPath
      numberOfWorkers: 1
      // Origin reserved to Cloudflare (ADR-0062): an empty list and "Allow" when the restriction is off.
      ipSecurityRestrictions: restrictToCloudflare ? cloudflareRules : []
      ipSecurityRestrictionsDefaultAction: restrictToCloudflare ? 'Deny' : 'Allow'
      // The SCM site (zip deployment from GitHub runners, whose addresses change) never inherits the main rules.
      scmIpSecurityRestrictionsDefaultAction: 'Allow'
      scmIpSecurityRestrictionsUseMain: false
      // The connection string is read from the component here so it never appears in outputs.
      appSettings: concat(appSettingsList, [
        {
          name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
          value: appInsights.properties.ConnectionString
        }
      ])
    }
  }
}

// File system logs (stdout of the container) for the Log stream, kept 7 days within 35 MB.
resource logs 'Microsoft.Web/sites/config@2025-03-01' = {
  parent: webApp
  name: 'logs'
  properties: {
    applicationLogs: {
      fileSystem: {
        level: 'Warning'
      }
    }
    httpLogs: {
      fileSystem: {
        enabled: true
        retentionInDays: 7
        retentionInMb: 35
      }
    }
    detailedErrorMessages: {
      enabled: false
    }
    failedRequestsTracing: {
      enabled: false
    }
  }
}

// Basic authentication (publishing user / password) disabled for FTP and SCM: deployments use
// a Microsoft Entra token (azure/login with OIDC), which the Kudu zip deploy accepts.
resource ftpBasicAuth 'Microsoft.Web/sites/basicPublishingCredentialsPolicies@2025-03-01' = {
  parent: webApp
  name: 'ftp'
  properties: {
    allow: false
  }
}

resource scmBasicAuth 'Microsoft.Web/sites/basicPublishingCredentialsPolicies@2025-03-01' = {
  parent: webApp
  name: 'scm'
  properties: {
    allow: false
  }
}

output name string = webApp.name
output id string = webApp.id
output defaultHostName string = webApp.properties.defaultHostName
output principalId string = webApp.identity.principalId
output customDomainVerificationId string = webApp.properties.customDomainVerificationId
