// Azure Communication Services + Email Communication Service.
// An Azure-managed domain (xxxx.azurecomm.net) works immediately but has low sending limits;
// the agency's own domain is added later (emailCustomDomain), verified in DNS, then linked
// (emailCustomDomainLinked) — see infra/README.md.

@description('Tags applied to every resource.')
param tags object

@description('Email Communication Service name.')
param emailServiceName string

@description('Communication Services resource name (globally unique).')
param communicationServiceName string

@description('Data location (at rest) of the communication resources, for example France or Europe.')
param dataLocation string

@description('Display name of the sender (shown by mail clients).')
param senderDisplayName string

@description('Optional custom email domain (for example agence-exemple.tn). Empty: Azure-managed domain only.')
param customDomainName string

@description('Link the custom domain and use it as sender, only once its DNS records are verified.')
param linkCustomDomain bool

var hasCustomDomain = !empty(customDomainName)
var useCustomDomain = hasCustomDomain && linkCustomDomain

resource emailService 'Microsoft.Communication/emailServices@2025-05-01' = {
  name: emailServiceName
  location: 'global'
  tags: tags
  properties: {
    dataLocation: dataLocation
  }
}

resource azureManagedDomain 'Microsoft.Communication/emailServices/domains@2025-05-01' = {
  parent: emailService
  name: 'AzureManagedDomain'
  location: 'global'
  tags: tags
  properties: {
    domainManagement: 'AzureManaged'
    userEngagementTracking: 'Disabled'
  }
}

// The Azure-managed domain comes with a "DoNotReply" sender; this only sets its display name.
resource azureManagedSender 'Microsoft.Communication/emailServices/domains/senderUsernames@2025-05-01' = {
  parent: azureManagedDomain
  name: 'donotreply'
  properties: {
    username: 'DoNotReply'
    displayName: senderDisplayName
  }
}

resource customDomain 'Microsoft.Communication/emailServices/domains@2025-05-01' = if (hasCustomDomain) {
  parent: emailService
  name: hasCustomDomain ? customDomainName : 'unused-custom-domain'
  location: 'global'
  tags: tags
  properties: {
    domainManagement: 'CustomerManaged'
    userEngagementTracking: 'Disabled'
  }
}

resource customSender 'Microsoft.Communication/emailServices/domains/senderUsernames@2025-05-01' = if (hasCustomDomain) {
  parent: customDomain
  name: 'noreply'
  properties: {
    username: 'noreply'
    displayName: senderDisplayName
  }
}

resource communicationService 'Microsoft.Communication/communicationServices@2025-05-01' = {
  name: communicationServiceName
  location: 'global'
  tags: tags
  properties: {
    dataLocation: dataLocation
    linkedDomains: useCustomDomain
      ? [
          azureManagedDomain.id
          customDomain.id
        ]
      : [
          azureManagedDomain.id
        ]
  }
}

output name string = communicationService.name
output endpoint string = 'https://${communicationService.properties.hostName}'
output senderAddress string = useCustomDomain
  ? 'noreply@${customDomainName}'
  : '${azureManagedSender.properties.username}@${azureManagedDomain.properties.mailFromSenderDomain}'
