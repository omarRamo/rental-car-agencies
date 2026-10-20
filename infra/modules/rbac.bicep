// Role assignments. The principal id arrives as a module parameter so that every assignment
// name (a deterministic GUID) can be computed when the deployment starts.

@description('Object id of the Web App system-assigned managed identity.')
param webAppPrincipalId string

@description('Storage account holding the private containers.')
param storageAccountName string

@description('Key Vault holding the secrets and the Data Protection key.')
param keyVaultName string

@description('Communication Services resource used to send e-mail.')
param communicationServiceName string

@description('Object id of the administrators Microsoft Entra group (also the SQL admin).')
param adminGroupObjectId string

@description('Grant the administrators group "Key Vault Secrets Officer" so its members can set application secrets.')
param grantAdminGroupKeyVaultSecretsOfficer bool

// Built-in role definition ids (identical in every tenant).
var roleIds = {
  storageBlobDataContributor: 'ba92f5b4-2d11-453d-a403-e96b0029c9fe'
  keyVaultSecretsUser: '4633458b-17de-408a-b874-0445c86b69e6'
  keyVaultCryptoUser: '12338af0-0e69-4776-bea7-57ae8d297424'
  keyVaultSecretsOfficer: 'b86a8fe4-44ce-4948-aee5-eccb2c155cd7'
}

resource storageAccount 'Microsoft.Storage/storageAccounts@2025-06-01' existing = {
  name: storageAccountName
}

resource vault 'Microsoft.KeyVault/vaults@2025-05-01' existing = {
  name: keyVaultName
}

resource communicationService 'Microsoft.Communication/communicationServices@2025-05-01' existing = {
  name: communicationServiceName
}

// Blobs (documents, inspections, contracts, exports) and the Data Protection key ring.
// Includes generateUserDelegationKey, needed for the 5-minute user delegation SAS links.
resource webAppBlobData 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storageAccount.id, webAppPrincipalId, roleIds.storageBlobDataContributor)
  scope: storageAccount
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roleIds.storageBlobDataContributor)
    principalId: webAppPrincipalId
    principalType: 'ServicePrincipal'
    description: 'Web App: private blob containers and Data Protection key ring.'
  }
}

// Secrets: Key Vault configuration provider and @Microsoft.KeyVault(...) app setting references.
resource webAppSecrets 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(vault.id, webAppPrincipalId, roleIds.keyVaultSecretsUser)
  scope: vault
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roleIds.keyVaultSecretsUser)
    principalId: webAppPrincipalId
    principalType: 'ServicePrincipal'
    description: 'Web App: read application secrets.'
  }
}

// Keys: wrap / unwrap of the Data Protection keys with the RSA key.
resource webAppCrypto 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(vault.id, webAppPrincipalId, roleIds.keyVaultCryptoUser)
  scope: vault
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roleIds.keyVaultCryptoUser)
    principalId: webAppPrincipalId
    principalType: 'ServicePrincipal'
    description: 'Web App: wrap and unwrap the Data Protection keys.'
  }
}

// Sending e-mail with a Microsoft Entra token (EmailClient + DefaultAzureCredential) requires
// these Communication Services permissions; a custom role avoids granting Contributor
// (which would also allow listing the access keys or deleting the resource).
resource emailSenderRole 'Microsoft.Authorization/roleDefinitions@2022-04-01' = {
  name: guid(resourceGroup().id, 'carrental-acs-email-sender')
  properties: {
    roleName: 'CarRental ACS Email Sender (${resourceGroup().name})'
    description: 'Send e-mail through Azure Communication Services with a Microsoft Entra identity.'
    type: 'CustomRole'
    permissions: [
      {
        actions: [
          'Microsoft.Communication/CommunicationServices/Read'
          'Microsoft.Communication/CommunicationServices/Write'
          'Microsoft.Communication/EmailServices/write'
        ]
        notActions: []
        dataActions: []
        notDataActions: []
      }
    ]
    assignableScopes: [
      resourceGroup().id
    ]
  }
}

resource webAppEmail 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(communicationService.id, webAppPrincipalId, 'carrental-acs-email-sender')
  scope: communicationService
  properties: {
    // Canonical subscription-level id of the custom role (role definitions are subscription objects).
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', emailSenderRole.name)
    principalId: webAppPrincipalId
    principalType: 'ServicePrincipal'
    description: 'Web App: send e-mail (outbox).'
  }
}

resource adminGroupSecretsOfficer 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (grantAdminGroupKeyVaultSecretsOfficer) {
  name: guid(vault.id, adminGroupObjectId, roleIds.keyVaultSecretsOfficer)
  scope: vault
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roleIds.keyVaultSecretsOfficer)
    principalId: adminGroupObjectId
    principalType: 'Group'
    description: 'Administrators: manage application secrets (Clic to Pay, WhatsApp, Anthropic, Turnstile...).'
  }
}
