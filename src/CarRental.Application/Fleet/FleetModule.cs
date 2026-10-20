using Microsoft.Extensions.DependencyInjection;

namespace CarRental.Application.Fleet;

/// <summary>Fleet module: vehicle categories (ACRISS), vehicles, documents and deadlines, maintenance. Built in Phase 1; registration is empty until then.</summary>
public static class FleetModule
{
    public static IServiceCollection AddFleetModule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services;
    }
}
