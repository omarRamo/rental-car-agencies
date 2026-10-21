using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Common.Auditing;
using CarRental.Application.Common.BackgroundJobs;
using CarRental.Application.Common.Persistence;
using CarRental.Application.Common.Security;
using CarRental.Application.Common.Seeding;
using CarRental.Application.Fleet.Contracts;
using CarRental.Application.Payments.Contracts;
using CarRental.Application.Referential.Contracts;
using CarRental.Application.Reporting.Contracts;
using CarRental.Application.Reporting.Funnel;
using CarRental.Application.Reporting.Indicators;
using CarRental.Application.Reporting.Snapshots;
using CarRental.Application.Reporting.Sources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CarRental.Application.Reporting;

/// <summary>
/// Reporting module: the funnel measurement of the public site (phase 2: <see cref="IFunnelEvents"/>, its retention
/// setting and nightly purge), the daily snapshots of the vehicles (nightly job, recomputation, ADR-0045), the indicators
/// of the reports screen and their ports to the packages not merged yet (ADR-0046).
/// </summary>
public static class ReportingModule
{
    public static IServiceCollection AddReportingModule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IFunnelEvents, FunnelEventRecorder>();
        services.AddRecurringJob<FunnelEventRetentionJob>();

        // Ports (ADR-0046). The fleet costs are one source of direct costs among others (3B adds the expenses with
        // TryAddEnumerable); the other ports are empty until their provider registers its own implementation, which wins
        // over these defaults (registered earlier by AddApplication, or later with AddScoped).
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IVehicleCostsSource, FleetVehicleCostsSource>());
        services.TryAddScoped<IChannelCommissionSource, UnconnectedChannelCommissions>();
        services.TryAddScoped<IInvoicedAmountsSource, UnconnectedInvoicedAmounts>();
        services.TryAddScoped<IBookingChargesSource, UnconnectedBookingCharges>();

        // Daily snapshots (ADR-0045).
        services.AddScoped<SnapshotInputsLoader>();
        services.AddScoped<SnapshotWriter>();
        services.AddScoped(sp => new SnapshotService(
            sp.GetRequiredService<IUserContext>(),
            sp.GetRequiredService<IReferentialQueries>(),
            sp.GetRequiredService<SnapshotWriter>(),
            sp.GetRequiredService<IAuditTrail>(),
            sp.GetRequiredService<TimeProvider>()));
        services.AddRecurringJob<DailySnapshotJob>();
        services.AddDataSeeder<ReportingDemoSeeder>();

        // Indicators of the reports screen and its exports (ADR-0046).
        services.AddScoped(sp => new ReportQueries(
            sp.GetRequiredService<IUserContext>(),
            sp.GetRequiredService<IReferentialQueries>(),
            sp.GetRequiredService<IAppDbContext>(),
            sp.GetRequiredService<IBookingsReportingQueries>(),
            sp.GetRequiredService<IFleetReportingQueries>(),
            sp.GetRequiredService<IFleetQueries>(),
            sp.GetRequiredService<IPaymentsQueries>(),
            sp.GetRequiredService<IPaymentsReportingQueries>(),
            sp.GetRequiredService<IInvoicedAmountsSource>(),
            sp.GetRequiredService<IBookingChargesSource>(),
            sp.GetRequiredService<IChannelCommissionSource>(),
            sp.GetRequiredService<IEnumerable<IVehicleCostsSource>>(),
            sp.GetRequiredService<ISettingsReader>(),
            sp.GetRequiredService<TimeProvider>()));

        services.AddSettingDefinitions(ReportingSettings.All);
        return services;
    }
}
