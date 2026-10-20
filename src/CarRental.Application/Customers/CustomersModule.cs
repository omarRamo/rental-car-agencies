using Microsoft.Extensions.DependencyInjection;

namespace CarRental.Application.Customers;

/// <summary>Customers module: customers, drivers, identity documents, consents and blacklist. Built in Phase 1; registration is empty until then.</summary>
public static class CustomersModule
{
    public static IServiceCollection AddCustomersModule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services;
    }
}
