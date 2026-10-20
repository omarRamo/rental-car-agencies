// Monthly cost budget on the resource group, with e-mail alerts (actual 80 %, forecast 100 %).
// Amounts are expressed in the billing currency of the subscription.

@description('Budget name.')
param budgetName string

@description('Monthly amount, in the billing currency.')
@minValue(1)
param amount int

@description('E-mail addresses notified by the budget alerts.')
@minLength(1)
param contactEmails array

@description('First day of the current month (yyyy-MM-01); a budget cannot start in a past month.')
param startDate string

resource budget 'Microsoft.Consumption/budgets@2026-06-01' = {
  name: budgetName
  properties: {
    category: 'Cost'
    amount: amount
    timeGrain: 'Monthly'
    timePeriod: {
      startDate: startDate
    }
    notifications: {
      actualAbove80Percent: {
        enabled: true
        operator: 'GreaterThan'
        threshold: 80
        thresholdType: 'Actual'
        contactEmails: contactEmails
      }
      forecastAbove100Percent: {
        enabled: true
        operator: 'GreaterThan'
        threshold: 100
        thresholdType: 'Forecasted'
        contactEmails: contactEmails
      }
    }
  }
}
