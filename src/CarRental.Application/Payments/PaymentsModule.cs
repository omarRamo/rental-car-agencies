using CarRental.Application.Payments.Administration;
using CarRental.Application.Payments.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace CarRental.Application.Payments;

/// <summary>
/// Payments module (ADR-0034). Phase 1: payments and refunds at the counter (cash through the cash session of the agent),
/// security deposits (a liability, never revenue), lists of the back office and totals for the reconciliations.
/// Phase 2 adds Clic to Pay on the same model (status only from the payment gateway, invariant 7).
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
        services.AddScoped<IPaymentsReportingQueries, PaymentsReportingQueries>();

        // Back office.
        services.AddScoped<PaymentAdminQueries>();
        return services;
    }
}
