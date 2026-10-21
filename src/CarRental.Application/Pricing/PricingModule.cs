using CarRental.Application.Common.Caching;
using CarRental.Application.Common.Persistence;
using CarRental.Application.Common.Security;
using CarRental.Application.Common.Seeding;
using CarRental.Application.Pricing.Administration;
using CarRental.Application.Pricing.Contracts;
using CarRental.Application.Pricing.Seeding;
using CarRental.Application.Referential.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace CarRental.Application.Pricing;

/// <summary>
/// Pricing module: versioned rate plans (seasons, events, price grid, tiers, extras, promotions, channel rules), the
/// pricing engine — the only place where prices are computed — and its simulator (ADR-0023, ADR-0024).
/// </summary>
public static class PricingModule
{
    public static IServiceCollection AddPricingModule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddScoped<IRatePlanStore, EfRatePlanStore>();
        services.AddSingleton<PublishedPlanCache>();
        services.AddSingleton<ICommittedChangesListener>(serviceProvider => serviceProvider.GetRequiredService<PublishedPlanCache>());
        services.AddScoped<IPublishedPlanSource, CachedPublishedPlanSource>();
        services.AddScoped<RatePlanSelector>();
        services.AddScoped<QuoteCalculator>();
        services.AddScoped<IPricingService, PricingService>();
        services.AddScoped<IPricingQueries, PricingQueries>();
        services.AddScoped(serviceProvider => new RatePlanAdministrationService(
            serviceProvider.GetRequiredService<IRatePlanStore>(),
            serviceProvider.GetRequiredService<QuoteCalculator>(),
            serviceProvider.GetRequiredService<IReferentialQueries>(),
            serviceProvider.GetRequiredService<IUserContext>(),
            serviceProvider.GetRequiredService<IUnitOfWork>(),
            serviceProvider.GetRequiredService<TimeProvider>()));
        services.AddSettingDefinitions(PricingSettings.All);
        services.AddDataSeeder<PricingDemoSeeder>();
        return services;
    }
}
