using Microsoft.Extensions.DependencyInjection;

namespace CarRental.Application.Payments;

/// <summary>Payments module: payments and deposits, Clic to Pay pre-authorization and capture. Built in Phase 2; registration is empty until then.</summary>
public static class PaymentsModule
{
    public static IServiceCollection AddPaymentsModule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services;
    }
}
