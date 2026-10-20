// Key Vault (RBAC authorization) holding the application secrets and the RSA key that wraps
// the ASP.NET Core Data Protection key ring (ProtectKeysWithAzureKeyVault).

@description('Azure region.')
param location string

@description('Tags applied to every resource.')
param tags object

@description('Key Vault name (3-24 characters, globally unique).')
@minLength(3)
@maxLength(24)
param keyVaultName string

@description('Soft delete retention in days (cannot be changed after creation).')
@minValue(7)
@maxValue(90)
param softDeleteRetentionInDays int

@description('Purge protection (irreversible once enabled): on in production, off in recette so the vault can be purged and recreated.')
param enablePurgeProtection bool

@description('Name of the RSA key used to wrap the Data Protection keys.')
param dataProtectionKeyName string

@description('Log Analytics workspace receiving the vault audit logs.')
param logAnalyticsWorkspaceId string

resource vault 'Microsoft.KeyVault/vaults@2025-05-01' = {
  name: keyVaultName
  location: location
  tags: tags
  properties: {
    tenantId: tenant().tenantId
    sku: {
      family: 'A'
      name: 'standard'
    }
    enableRbacAuthorization: true
    enableSoftDelete: true
    softDeleteRetentionInDays: softDeleteRetentionInDays
    // The API rejects an explicit "false" once purge protection exists, so omit it instead.
    enablePurgeProtection: enablePurgeProtection ? true : null
    enabledForDeployment: false
    enabledForDiskEncryption: false
    enabledForTemplateDeployment: false
    publicNetworkAccess: 'Enabled'
    networkAcls: {
      defaultAction: 'Allow'
      bypass: 'AzureServices'
    }
  }
}

// Created through the control plane ("create if not exists"): a redeployment never creates a
// new version. Rotation is automatic (yearly) and keeps the previous versions enabled, so key
// ring entries wrapped with an older version can still be unwrapped.
resource dataProtectionKey 'Microsoft.KeyVault/vaults/keys@2025-05-01' = {
  parent: vault
  name: dataProtectionKeyName
  tags: tags
  properties: {
    kty: 'RSA'
    keySize: 3072
    keyOps: [
      'wrapKey'
      'unwrapKey'
    ]
    attributes: {
      enabled: true
    }
    rotationPolicy: {
      attributes: {
        expiryTime: 'P2Y'
      }
      lifetimeActions: [
        {
          trigger: {
            timeAfterCreate: 'P1Y'
          }
          action: {
            type: 'rotate'
          }
        }
        {
          trigger: {
            timeBeforeExpiry: 'P30D'
          }
          action: {
            type: 'notify'
          }
        }
      ]
    }
  }
}

resource auditLogs 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = {
  name: 'audit-to-log-analytics'
  scope: vault
  properties: {
    workspaceId: logAnalyticsWorkspaceId
    logs: [
      {
        categoryGroup: 'audit'
        enabled: true
      }
    ]
  }
}

output name string = vault.name
output uri string = vault.properties.vaultUri
@description('Versionless key URI: the Data Protection encryptor always wraps with the current version.')
output dataProtectionKeyUri string = dataProtectionKey.properties.keyUri
