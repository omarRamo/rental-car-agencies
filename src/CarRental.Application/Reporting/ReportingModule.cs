using CarRental.Application.Common.BackgroundJobs;
using CarRental.Application.Referential.Contracts;
using CarRental.Application.Reporting.Contracts;
using CarRental.Application.Reporting.Funnel;
using Microsoft.Extensions.DependencyInjection;

namespace CarRental.Application.Reporting;

/// <summary>
/// Reporting module: daily snapshot, indicators and exports (phase 3). Phase 2 brings the funnel measurement of the
/// public site: <see cref="IFunnelEvents"/>, its retention setting and the nightly purge.
/// </summary>
public static class ReportingModule
{
    public static IServiceCollection AddReportingModule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IFunnelEvents, FunnelEventRecorder>();
        services.AddRecurringJob<FunnelEventRetentionJob>();
        services.AddSettingDefinitions(ReportingSettings.All);
        return services;
    }
}
