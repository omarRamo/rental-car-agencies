// Storage account for private blobs: identity documents, inspection photos, contracts,
// exports and the ASP.NET Core Data Protection key ring.
// Shared key (account key / account SAS) access is disabled: every client authenticates with
// Microsoft Entra ID (managed identity in Azure, `az ... --auth-mode login` for humans).
// Short-lived download links must therefore be *user delegation* SAS (see infra/README.md).

@description('Azure region.')
param location string

@description('Tags applied to every resource.')
param tags object

@description('Storage account name (3-24 lowercase letters and digits, globally unique).')
@minLength(3)
@maxLength(24)
param storageAccountName string

@description('Replication: Standard_LRS (recette), Standard_GRS (production, copy in France South), Standard_ZRS.')
@allowed([
  'Standard_LRS'
  'Standard_ZRS'
  'Standard_GRS'
  'Standard_GZRS'
])
param skuName string

@description('Private blob containers to create.')
param containerNames array

@description('Blob soft delete retention, in days.')
@minValue(1)
@maxValue(365)
param blobSoftDeleteDays int

@description('Container soft delete retention, in days.')
@minValue(1)
@maxValue(365)
param containerSoftDeleteDays int

@description('Point-in-time restore window for block blobs, in days (0 disables it; must be lower than the blob soft delete retention).')
@minValue(0)
@maxValue(364)
param pointInTimeRestoreDays int

@description('Previous blob versions older than this many days are deleted by a lifecycle rule (0 keeps every version forever).')
@minValue(0)
param versionRetentionDays int

var pointInTimeRestoreEnabled = pointInTimeRestoreDays > 0

resource storageAccount 'Microsoft.Storage/storageAccounts@2025-06-01' = {
  name: storageAccountName
  location: location
  tags: tags
  kind: 'StorageV2'
  sku: {
    name: skuName
  }
  properties: {
    accessTier: 'Hot'
    allowBlobPublicAccess: false
    allowSharedKeyAccess: false
    defaultToOAuthAuthentication: true
    allowCrossTenantReplication: false
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
    publicNetworkAccess: 'Enabled'
    networkAcls: {
      // No private endpoint at this budget: the endpoint stays public but only accepts
      // Entra-authenticated requests (no anonymous access, no account key).
      defaultAction: 'Allow'
      bypass: 'AzureServices'
    }
    encryption: {
      keySource: 'Microsoft.Storage'
      services: {
        blob: {
          enabled: true
          keyType: 'Account'
        }
      }
    }
  }
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2025-06-01' = {
  parent: storageAccount
  name: 'default'
  properties: {
    isVersioningEnabled: true
    deleteRetentionPolicy: {
      enabled: true
      days: blobSoftDeleteDays
      allowPermanentDelete: false
    }
    containerDeleteRetentionPolicy: {
      enabled: true
      days: containerSoftDeleteDays
    }
    // Point-in-time restore needs versioning + soft delete + change feed.
    changeFeed: {
      enabled: pointInTimeRestoreEnabled
      retentionInDays: pointInTimeRestoreEnabled ? blobSoftDeleteDays : null
    }
    restorePolicy: {
      enabled: pointInTimeRestoreEnabled
      days: pointInTimeRestoreEnabled ? max(pointInTimeRestoreDays, 1) : null // max() only narrows the type
    }
  }
}

resource containers 'Microsoft.Storage/storageAccounts/blobServices/containers@2025-06-01' = [
  for containerName in containerNames: {
    parent: blobService
    name: containerName
    properties: {
      publicAccess: 'None'
    }
  }
]

// Keeps versioning affordable: only *previous* versions are deleted, never the current blob.
// The purge of identity documents themselves stays an application feature, disabled by default.
resource lifecycle 'Microsoft.Storage/storageAccounts/managementPolicies@2025-06-01' = if (versionRetentionDays > 0) {
  parent: storageAccount
  name: 'default'
  properties: {
    policy: {
      rules: [
        {
          name: 'delete-old-previous-versions'
          enabled: true
          type: 'Lifecycle'
          definition: {
            filters: {
              blobTypes: [
                'blockBlob'
              ]
            }
            actions: {
              version: {
                delete: {
                  daysAfterCreationGreaterThan: versionRetentionDays
                }
              }
            }
          }
        }
      ]
    }
  }
}

output name string = storageAccount.name
output blobEndpoint string = storageAccount.properties.primaryEndpoints.blob
