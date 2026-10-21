using CarRental.Application.Common.BackgroundJobs;
using CarRental.Application.Payments.Administration;
using CarRental.Application.Payments.Contracts;
using CarRental.Application.Payments.Online;
using CarRental.Application.Referential.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace CarRental.Application.Payments;

/// <summary>
/// Payments module (ADR-0034, ADR-0038, ADR-0039). Phase 1: payments and refunds at the counter (cash through the cash
/// session of the agent), security deposits (a liability, never revenue), lists of the back office and totals for the
/// reconciliations. Phase 2: online payments with Clic to Pay (pre-authorization, capture, reversal, one-step payments
/// and links, online refunds with the approval of a manager above a threshold), the catch-up job and the daily
/// reconciliation; the status of an online payment only comes from the gateway (invariant 7). Infrastructure provides
/// <see cref="IClicToPayGateway"/> and <see cref="IGatewayCallLog"/>.
/// </summary>
public static class PaymentsModule
{
    public static IServiceCollection AddPaymentsModule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Contracts for the other modules and the back office.
        services.AddScoped<ICounterPayments, CounterPayments>();
        services.AddScoped<IDeposits, Deposits>();
        services.AddScoped<IPaymentsQueries, PaymentsQueries>();
        services.AddScoped<OnlinePayments>();
        services.AddScoped<IOnlinePayments>(serviceProvider => serviceProvider.GetRequiredService<OnlinePayments>());

        // Back office.
        services.AddScoped<PaymentAdminQueries>();
        services.AddScoped<OnlinePaymentAdministration>();

        services.AddSettingDefinitions(PaymentSettings.All);
        services.AddRecurringJob<OnlinePaymentCatchUpJob>();
        services.AddRecurringJob<OnlinePaymentReconciliationJob>();
        return services;
    }
}
