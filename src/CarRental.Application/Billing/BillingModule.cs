using Microsoft.Extensions.DependencyInjection;

namespace CarRental.Application.Billing;

/// <summary>Billing module: invoices and credit notes (gapless numbering), TTN e-invoicing. Built in Phase 3; registration is empty until then.</summary>
public static class BillingModule
{
    public static IServiceCollection AddBillingModule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services;
    }
}
