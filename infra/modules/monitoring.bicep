// Log Analytics workspace + workspace-based Application Insights.
// The daily cap is set on the workspace: Application Insights telemetry is stored in the
// workspace, so the cap stops its ingestion too (recommended over the classic component cap).
// The OpenTelemetry distro (Azure.Monitor.OpenTelemetry.AspNetCore) only needs the
// connection string; no App Service auto-instrumentation agent is enabled.

@description('Azure region.')
param location string

@description('Tags applied to every resource.')
param tags object

@description('Log Analytics workspace name.')
param workspaceName string

@description('Application Insights component name.')
param appInsightsName string

@description('Daily ingestion cap of the workspace in GB, as a decimal string (Bicep has no float literal), for example "0.15".')
param dailyCapGb string

@description('Interactive retention of the workspace, in days (30 or 31 days are included in the price).')
@minValue(30)
@maxValue(730)
param retentionInDays int

resource workspace 'Microsoft.OperationalInsights/workspaces@2025-07-01' = {
  name: workspaceName
  location: location
  tags: tags
  properties: {
    sku: {
      name: 'PerGB2018'
    }
    retentionInDays: retentionInDays
    workspaceCapping: {
      dailyQuotaGb: json(dailyCapGb)
    }
    features: {
      enableLogAccessUsingOnlyResourcePermissions: true
    }
    publicNetworkAccessForIngestion: 'Enabled'
    publicNetworkAccessForQuery: 'Enabled'
  }
}

resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: appInsightsName
  location: location
  tags: tags
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: workspace.id
    IngestionMode: 'LogAnalytics'
    publicNetworkAccessForIngestion: 'Enabled'
    publicNetworkAccessForQuery: 'Enabled'
    // Local (connection string) authentication stays enabled: the app only receives
    // APPLICATIONINSIGHTS_CONNECTION_STRING. Switching to Entra-only ingestion requires the
    // "Monitoring Metrics Publisher" role and a TokenCredential in the exporter (see infra/README.md).
    DisableLocalAuth: false
  }
}

output workspaceId string = workspace.id
output appInsightsName string = appInsights.name
