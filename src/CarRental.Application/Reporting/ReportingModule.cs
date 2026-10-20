using Microsoft.Extensions.DependencyInjection;

namespace CarRental.Application.Reporting;

/// <summary>Reporting module: daily snapshot, indicators and exports. Built in Phase 3; registration is empty until then.</summary>
public static class ReportingModule
{
    public static IServiceCollection AddReportingModule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services;
    }
}
