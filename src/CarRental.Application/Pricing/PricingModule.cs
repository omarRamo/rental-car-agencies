using Microsoft.Extensions.DependencyInjection;

namespace CarRental.Application.Pricing;

/// <summary>Pricing module: seasons, rate plans, extras, promotions, channel rates and taxes; the only place where prices are computed. Built in Phase 1; registration is empty until then.</summary>
public static class PricingModule
{
    public static IServiceCollection AddPricingModule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services;
    }
}
