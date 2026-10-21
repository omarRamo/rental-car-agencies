using CarRental.Application.Common.BackgroundJobs;
using CarRental.Application.Common.Seeding;
using CarRental.Application.Fleet.Contracts;
using CarRental.Application.Fleet.Deadlines;
using CarRental.Application.Fleet.Photos;
using CarRental.Application.Fleet.Seeding;
using CarRental.Application.Referential.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace CarRental.Application.Fleet;

/// <summary>
/// Fleet module: vehicle categories (ACRISS), vehicles, documents and deadlines, maintenance, economics and photos;
/// rentable windows for the availability engine (<see cref="IFleetQueries"/>); daily deadline alerts to the managers.
/// The web host provides <see cref="IDeadlineAlertEmailFactory"/> (localized e-mails).
/// </summary>
public static class FleetModule
{
    public static IServiceCollection AddFleetModule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddScoped<IFleetQueries, FleetQueries>();
        services.AddScoped<IFleetReportingQueries, FleetReportingQueries>();
        services.AddScoped<FleetWriter>();
        services.AddScoped<FleetAdminQueries>();
        services.AddScoped<VehicleCategoryService>();
        services.AddScoped<VehicleService>();
        services.AddScoped<VehicleMaintenanceService>();
        services.AddScoped<FleetPhotoService>();
        services.AddSettingDefinitions(FleetSettings.All);
        services.AddRecurringJob<DeadlineAlertJob>();
        services.AddDataSeeder<FleetDemoSeeder>();
        return services;
    }
}
