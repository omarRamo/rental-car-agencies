using CarRental.Application.Common.Seeding;
using CarRental.Application.Referential.Contracts;
using CarRental.Application.Referential.Seeding;
using Microsoft.Extensions.DependencyInjection;

namespace CarRental.Application.Referential;

/// <summary>Referential module: agencies, pick-up/return locations and business settings (SPEC §13 "Référentiel").</summary>
public static class ReferentialModule
{
    public static IServiceCollection AddReferentialModule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddScoped<IReferentialQueries, ReferentialQueries>();
        services.AddScoped<ISettingsReader, SettingsReader>();
        services.AddSettingDefinitions(WellKnownSettings.All);
        services.AddDataSeeder<ReferentialSettingsSeeder>();
        services.AddDataSeeder<ReferentialDemoSeeder>();
        return services;
    }
}
