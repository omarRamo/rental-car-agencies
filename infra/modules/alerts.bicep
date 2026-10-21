// Alerting (ADR-0060): one action group (e-mail, optional SMS) and low-cost alert rules on the platform metrics of the
// Web App and the database, and on the application telemetry in Application Insights. Every rule is described in
// infra/README.md ("Alertes") with its first actions in docs/runbook.md.
//
// Cost (order of magnitude, to check in the Azure pricing calculator): metric alerts are billed per monitored time
// series, the first 10 series of the subscription being free (this module uses at most 5); log search alerts are
// billed per rule and per evaluation frequency (15 minutes here, the cheapest), extra dimension series apart; e-mail
// notifications are free up to 1,000 per month; SMS outside the United States is billed from the first message.
// The optional standard availability test is billed per execution.
//
// The application telemetry used here (ADR-0060):
//   carrental.health.status   gauge, one point per health entry and per minute (tag entry): 2 healthy, 1 degraded, 0 unhealthy
//   carrental.clictopay.calls counter of the Clic to Pay calls (tags operation, outcome: Succeeded, GatewayError, Unavailable, InvalidResponse)

@description('Azure region of the regional resources (log search alert rules, availability test): the region of Application Insights.')
param location string

@description('Tags applied to every resource.')
param tags object

@description('Prefix of the alert rule names, e.g. alert-carrental-prod.')
param namePrefix string

@description('Action group name.')
param actionGroupName string

@description('Action group short name (12 characters at most), shown in SMS and e-mails.')
@maxLength(12)
param actionGroupShortName string

@description('E-mail addresses notified by every alert.')
@minLength(1)
param alertEmails array

@description('Optional SMS receivers: objects { countryCode: \'216\', phoneNumber: \'20123456\' }. Billed per SMS.')
param alertSmsNumbers array = []

@description('Web App watched by the platform metric alerts.')
param webAppId string

@description('Application Insights component holding the application telemetry.')
param appInsightsId string

@description('Database watched by the capacity alerts.')
param sqlDatabaseId string

@description('The Web App has the App Service health check (not on the Free plan): watch its status.')
param healthCheckEnabled bool

@description('The application runs all the time (Always On, not on the Free plan): its telemetry never pauses, so its absence is a problem.')
param alwaysOn bool

@description('The database has a DTU-based SKU (Basic, S0…): DTU and storage alerts; the vCore free offer has other metrics.')
param sqlDtuBased bool

@description('More HTTP 5xx answers than this in 15 minutes raise an alert.')
@minValue(0)
param http5xxThreshold int = 10

@description('More exceptions than this in 15 minutes raise an alert.')
@minValue(0)
param exceptionsThreshold int = 20

@description('At least this many technical failures of the Clic to Pay calls in 15 minutes raise an alert.')
@minValue(1)
param paymentFailuresThreshold int = 3

@description('Average DTU percentage over 30 minutes above which the database is too small (upgrade to S0, infra/README.md).')
@minValue(1)
@maxValue(100)
param sqlDtuPercentThreshold int = 90

@description('Data space used (percent of the maximum size) above which the database must grow (Basic: 2 GB).')
@minValue(1)
@maxValue(100)
param sqlStoragePercentThreshold int = 80

@description('Public URL of /health checked by a standard availability test, e.g. https://www.example.tn/health (empty = no test: free external monitoring instead, see infra/README.md).')
param availabilityTestUrl string = ''

@description('Locations of the availability test (each one multiplies the executions billed). emea-fr-pra-edge = France Central.')
param availabilityTestLocations array = [
  'emea-fr-pra-edge'
]

var hasAvailabilityTest = !empty(availabilityTestUrl)
var availabilityTestName = '${namePrefix}-availability'

// KQL of the log search alerts. Multi-line strings are verbatim in Bicep: thresholds go in through format().
var healthQuery = '''
customMetrics
| where name == "carrental.health.status"
| extend entry = tostring(customDimensions["entry"])
| summarize best = max(value) by entry
| where best < 2
'''

var paymentFailuresQuery = format(
  '''
customMetrics
| where name == "carrental.clictopay.calls"
| extend outcome = tostring(customDimensions["outcome"])
| where outcome != "Succeeded"
| summarize failures = sum(value)
| where failures >= {0}
''',
  paymentFailuresThreshold
)

var exceptionsQuery = format(
  '''
exceptions
| summarize total = sum(itemCount)
| where total > {0}
''',
  exceptionsThreshold
)

var telemetryMissingQuery = '''
customMetrics
| where name == "carrental.health.status"
| summarize samples = count()
| where samples == 0
'''

resource actionGroup 'Microsoft.Insights/actionGroups@2023-01-01' = {
  name: actionGroupName
  location: 'global'
  tags: tags
  properties: {
    groupShortName: actionGroupShortName
    enabled: true
    emailReceivers: [
      for (email, i) in alertEmails: {
        name: 'email-${i}'
        emailAddress: email
        useCommonAlertSchema: true
      }
    ]
    smsReceivers: [
      for (sms, i) in alertSmsNumbers: {
        name: 'sms-${i}'
        countryCode: sms.countryCode
        phoneNumber: sms.phoneNumber
      }
    ]
  }
}

var metricActions = [
  {
    actionGroupId: actionGroup.id
  }
]

var logActions = {
  actionGroups: [
    actionGroup.id
  ]
}

// ---------------------------------------------------------------------------------------------
// Platform metrics (free within the first 10 time series of the subscription)
// ---------------------------------------------------------------------------------------------

// The App Service health check probes /health/ready every minute; the metric shows a failure once the instance is
// deemed unhealthy (10 failed probes by default): the site is down or its database unreachable.
resource siteDown 'Microsoft.Insights/metricAlerts@2018-03-01' = if (healthCheckEnabled) {
  name: '${namePrefix}-site-down'
  location: 'global'
  tags: tags
  properties: {
    description: 'Site indisponible : le contrôle de santé d\'App Service (/health/ready) échoue. Runbook : alerte « site-down ».'
    severity: 1
    enabled: true
    scopes: [
      webAppId
    ]
    evaluationFrequency: 'PT5M'
    windowSize: 'PT15M'
    criteria: {
      'odata.type': 'Microsoft.Azure.Monitor.SingleResourceMultipleMetricCriteria'
      allOf: [
        {
          criterionType: 'StaticThresholdCriterion'
          name: 'HealthCheckStatus'
          metricNamespace: 'Microsoft.Web/sites'
          metricName: 'HealthCheckStatus'
          operator: 'LessThan'
          threshold: 100
          timeAggregation: 'Average'
        }
      ]
    }
    autoMitigate: true
    actions: metricActions
  }
}

resource http5xx 'Microsoft.Insights/metricAlerts@2018-03-01' = {
  name: '${namePrefix}-http-5xx'
  location: 'global'
  tags: tags
  properties: {
    description: 'Erreurs serveur : plus de ${http5xxThreshold} réponses HTTP 5xx en 15 minutes. Runbook : alerte « http-5xx ».'
    severity: 2
    enabled: true
    scopes: [
      webAppId
    ]
    evaluationFrequency: 'PT5M'
    windowSize: 'PT15M'
    criteria: {
      'odata.type': 'Microsoft.Azure.Monitor.SingleResourceMultipleMetricCriteria'
      allOf: [
        {
          criterionType: 'StaticThresholdCriterion'
          name: 'Http5xx'
          metricNamespace: 'Microsoft.Web/sites'
          metricName: 'Http5xx'
          operator: 'GreaterThan'
          threshold: http5xxThreshold
          timeAggregation: 'Total'
        }
      ]
    }
    autoMitigate: true
    actions: metricActions
  }
}

resource sqlDtu 'Microsoft.Insights/metricAlerts@2018-03-01' = if (sqlDtuBased) {
  name: '${namePrefix}-sql-dtu'
  location: 'global'
  tags: tags
  properties: {
    description: 'Base saturée : DTU moyenne au-dessus de ${sqlDtuPercentThreshold} % pendant 30 minutes. Runbook : alerte « sql-capacity ».'
    severity: 3
    enabled: true
    scopes: [
      sqlDatabaseId
    ]
    evaluationFrequency: 'PT15M'
    windowSize: 'PT30M'
    criteria: {
      'odata.type': 'Microsoft.Azure.Monitor.SingleResourceMultipleMetricCriteria'
      allOf: [
        {
          criterionType: 'StaticThresholdCriterion'
          name: 'DtuPercent'
          metricNamespace: 'Microsoft.Sql/servers/databases'
          metricName: 'dtu_consumption_percent'
          operator: 'GreaterThan'
          threshold: sqlDtuPercentThreshold
          timeAggregation: 'Average'
        }
      ]
    }
    autoMitigate: true
    actions: metricActions
  }
}

resource sqlStorage 'Microsoft.Insights/metricAlerts@2018-03-01' = if (sqlDtuBased) {
  name: '${namePrefix}-sql-storage'
  location: 'global'
  tags: tags
  properties: {
    description: 'Base presque pleine : plus de ${sqlStoragePercentThreshold} % de la taille maximale (2 Go en Basic). Runbook : alerte « sql-capacity ».'
    severity: 2
    enabled: true
    scopes: [
      sqlDatabaseId
    ]
    evaluationFrequency: 'PT1H'
    windowSize: 'PT1H'
    criteria: {
      'odata.type': 'Microsoft.Azure.Monitor.SingleResourceMultipleMetricCriteria'
      allOf: [
        {
          criterionType: 'StaticThresholdCriterion'
          name: 'StoragePercent'
          metricNamespace: 'Microsoft.Sql/servers/databases'
          metricName: 'storage_percent'
          operator: 'GreaterThan'
          threshold: sqlStoragePercentThreshold
          timeAggregation: 'Maximum'
        }
      ]
    }
    autoMitigate: true
    actions: metricActions
  }
}

// ---------------------------------------------------------------------------------------------
// Application telemetry (log search alerts every 15 minutes, the cheapest frequency)
// ---------------------------------------------------------------------------------------------

// The generic rule: any health entry (sql, storage, hangfire, jobs, outbox, channels, payments, and any entry added
// later, e.g. whatsapp) that was never healthy during the last 15 minutes. One alert per entry (dimension "entry").
// It covers the failed outbox messages and the blocked WhatsApp queue (outbox), the channel updates failed for good
// (channels), the payments flagged or unconfirmed (payments), the recurring jobs that failed or stopped, the daily
// fleet deadline e-mail included (jobs).
resource healthDegraded 'Microsoft.Insights/scheduledQueryRules@2023-12-01' = {
  name: '${namePrefix}-health-degraded'
  location: location
  tags: tags
  kind: 'LogAlert'
  properties: {
    displayName: '${namePrefix}-health-degraded'
    description: 'Une entrée de santé (dimension « entry ») n\'a pas été saine pendant 15 minutes. Runbook : alerte « health-degraded », section de l\'entrée.'
    severity: 2
    enabled: true
    evaluationFrequency: 'PT15M'
    windowSize: 'PT15M'
    scopes: [
      appInsightsId
    ]
    criteria: {
      allOf: [
        {
          query: healthQuery
          timeAggregation: 'Count'
          dimensions: [
            {
              name: 'entry'
              operator: 'Include'
              values: [
                '*'
              ]
            }
          ]
          operator: 'GreaterThan'
          threshold: 0
          failingPeriods: {
            numberOfEvaluationPeriods: 1
            minFailingPeriodsToAlert: 1
          }
        }
      ]
    }
    autoMitigate: true
    // The tables of a new Application Insights resource appear with the first data: validate at evaluation time only.
    skipQueryValidation: true
    actions: logActions
  }
}

// Technical failures of the gateway calls (no answer, unreadable answer, refusal of the request by the gateway):
// never a card declined by the bank, which is a successful status read.
resource paymentFailures 'Microsoft.Insights/scheduledQueryRules@2023-12-01' = {
  name: '${namePrefix}-payments-gateway'
  location: location
  tags: tags
  kind: 'LogAlert'
  properties: {
    displayName: '${namePrefix}-payments-gateway'
    description: 'Paiements en ligne : au moins ${paymentFailuresThreshold} appels Clic to Pay en échec technique en 15 minutes. Runbook : alerte « payments-gateway ».'
    severity: 1
    enabled: true
    evaluationFrequency: 'PT15M'
    windowSize: 'PT15M'
    scopes: [
      appInsightsId
    ]
    criteria: {
      allOf: [
        {
          query: paymentFailuresQuery
          timeAggregation: 'Count'
          operator: 'GreaterThan'
          threshold: 0
          failingPeriods: {
            numberOfEvaluationPeriods: 1
            minFailingPeriodsToAlert: 1
          }
        }
      ]
    }
    autoMitigate: true
    skipQueryValidation: true
    actions: logActions
  }
}

resource exceptions 'Microsoft.Insights/scheduledQueryRules@2023-12-01' = {
  name: '${namePrefix}-exceptions'
  location: location
  tags: tags
  kind: 'LogAlert'
  properties: {
    displayName: '${namePrefix}-exceptions'
    description: 'Pic d\'exceptions : plus de ${exceptionsThreshold} exceptions en 15 minutes. Runbook : alerte « exceptions ».'
    severity: 2
    enabled: true
    evaluationFrequency: 'PT15M'
    windowSize: 'PT15M'
    scopes: [
      appInsightsId
    ]
    criteria: {
      allOf: [
        {
          query: exceptionsQuery
          timeAggregation: 'Count'
          operator: 'GreaterThan'
          threshold: 0
          failingPeriods: {
            numberOfEvaluationPeriods: 1
            minFailingPeriodsToAlert: 1
          }
        }
      ]
    }
    autoMitigate: true
    skipQueryValidation: true
    actions: logActions
  }
}

// The application publishes its health every minute: no point for 30 minutes means it is stopped, or its telemetry
// no longer arrives (daily cap of the workspace reached, connection string). Only where the app never sleeps.
resource telemetryMissing 'Microsoft.Insights/scheduledQueryRules@2023-12-01' = if (alwaysOn) {
  name: '${namePrefix}-telemetry-missing'
  location: location
  tags: tags
  kind: 'LogAlert'
  properties: {
    displayName: '${namePrefix}-telemetry-missing'
    description: 'Télémétrie absente : aucune mesure de santé depuis 30 minutes (application arrêtée ou plafond journalier atteint). Runbook : alerte « telemetry-missing ».'
    severity: 2
    enabled: true
    evaluationFrequency: 'PT15M'
    windowSize: 'PT30M'
    scopes: [
      appInsightsId
    ]
    criteria: {
      allOf: [
        {
          query: telemetryMissingQuery
          timeAggregation: 'Count'
          operator: 'GreaterThan'
          threshold: 0
          failingPeriods: {
            numberOfEvaluationPeriods: 1
            minFailingPeriodsToAlert: 1
          }
        }
      ]
    }
    autoMitigate: true
    skipQueryValidation: true
    actions: logActions
  }
}

// ---------------------------------------------------------------------------------------------
// Optional availability test of the public site (billed per execution)
// ---------------------------------------------------------------------------------------------

resource availabilityTest 'Microsoft.Insights/webtests@2022-06-15' = if (hasAvailabilityTest) {
  name: availabilityTestName
  location: location
  // The hidden link ties the test to its Application Insights component (portal and alert criteria).
  tags: union(tags, {
    'hidden-link:${appInsightsId}': 'Resource'
  })
  kind: 'standard'
  properties: {
    SyntheticMonitorId: availabilityTestName
    Name: 'Site public /health'
    Description: 'GET /health toutes les 15 minutes (vivacité, sans base de données) par l\'adresse publique.'
    Enabled: true
    Frequency: 900
    Timeout: 30
    Kind: 'standard'
    RetryEnabled: true
    Locations: [
      for id in availabilityTestLocations: {
        Id: id
      }
    ]
    Request: {
      RequestUrl: availabilityTestUrl
      HttpVerb: 'GET'
      ParseDependentRequests: false
    }
    ValidationRules: {
      ExpectedHttpStatusCode: 200
      SSLCheck: true
      SSLCertRemainingLifetimeCheck: 7
    }
  }
}

resource availability 'Microsoft.Insights/metricAlerts@2018-03-01' = if (hasAvailabilityTest) {
  name: '${namePrefix}-availability'
  location: 'global'
  tags: tags
  properties: {
    description: 'Site public injoignable depuis Internet (test de disponibilité de /health). Runbook : alerte « availability ».'
    severity: 1
    enabled: true
    scopes: [
      availabilityTest.id
      appInsightsId
    ]
    evaluationFrequency: 'PT5M'
    windowSize: 'PT15M'
    criteria: {
      'odata.type': 'Microsoft.Azure.Monitor.WebtestLocationAvailabilityCriteria'
      webTestId: availabilityTest.id
      componentId: appInsightsId
      failedLocationCount: 1
    }
    autoMitigate: true
    actions: metricActions
  }
}

output actionGroupId string = actionGroup.id

@description('Names of the alert rules deployed (the suffixes are documented in infra/README.md, "Alertes").')
output alertNames array = map(
  filter(
    [
      { suffix: 'site-down', deployed: healthCheckEnabled }
      { suffix: 'http-5xx', deployed: true }
      { suffix: 'sql-dtu', deployed: sqlDtuBased }
      { suffix: 'sql-storage', deployed: sqlDtuBased }
      { suffix: 'health-degraded', deployed: true }
      { suffix: 'payments-gateway', deployed: true }
      { suffix: 'exceptions', deployed: true }
      { suffix: 'telemetry-missing', deployed: alwaysOn }
      { suffix: 'availability', deployed: hasAvailabilityTest }
    ],
    rule => rule.deployed
  ),
  rule => '${namePrefix}-${rule.suffix}'
)
