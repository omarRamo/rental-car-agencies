using System.Globalization;
using CarRental.Application.Common.BackgroundJobs;
using CarRental.Application.Common.Security;
using CarRental.Application.Referential.Contracts;
using CarRental.Application.Reporting.Contracts;
using CarRental.Domain.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CarRental.Application.Reporting.Snapshots;

/// <summary>
/// The nightly snapshot (ADR-0045): every night at 03:00, Tunis time, for each agency, computes the previous day again
/// with the <c>reporting.snapshot_refresh_days</c> − 1 days before it (7 days by default), so that a late recording
/// (return, no-show, cost) still reaches the snapshots. Each agency is computed in a scope of its own (its own DbContext),
/// restricted to that agency (ADR-0067); none is skipped, even when its reports module is off or it is suspended: the
/// snapshots are data, shown again when the module is back. An agency that fails does not stop the others; the run fails
/// at the end so that the dashboard of the jobs shows it.
/// </summary>
internal sealed partial class DailySnapshotJob(
    IServiceScopeFactory scopeFactory,
    IReferentialQueries referential,
    TimeProvider clock,
    ILogger<DailySnapshotJob> logger) : IRecurringJob
{
    public const string ActorName = "system:reporting-snapshot";

    public static string JobId => "reporting.daily_snapshot";

    /// <summary>Every night at 03:00, Tunis time.</summary>
    public static string CronExpression => "0 3 * * *";

    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        var yesterday = TunisTime.Today(clock).AddDays(-1);
        var failed = new List<Guid>();
        foreach (var agency in await referential.GetAgenciesAsync(cancellationToken))
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                scope.ServiceProvider.GetRequiredService<ScopedUserContext>().Initialize(UserContextSnapshot.System(ActorName, agencyId: agency.Id));
                var days = await scope.ServiceProvider.GetRequiredService<ISettingsReader>()
                    .GetAsync(ReportingSettings.SnapshotRefreshDays, agency.Id, cancellationToken);
                var run = await scope.ServiceProvider.GetRequiredService<SnapshotWriter>()
                    .RecomputeAsync(agency.Id, yesterday.AddDays(1 - days), yesterday, cancellationToken);
                if (run.IsFailure)
                {
                    failed.Add(agency.Id);
                    LogAgencyFailed(agency.Id, run.Error.Code);
                }
            }
#pragma warning disable CA1031 // One agency whose data breaks the computation must not deprive the others of their snapshots.
            catch (Exception exception) when (exception is not OperationCanceledException)
#pragma warning restore CA1031
            {
                failed.Add(agency.Id);
                LogAgencyCrashed(exception, agency.Id);
            }
        }

        if (failed.Count > 0)
        {
            throw new InvalidOperationException(string.Create(
                CultureInfo.InvariantCulture,
                $"The snapshots of {failed.Count} agency(ies) could not be computed: {string.Join(", ", failed)}."));
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Snapshots of agency {AgencyId} not computed: {Code}.")]
    private partial void LogAgencyFailed(Guid agencyId, string code);

    [LoggerMessage(Level = LogLevel.Error, Message = "Snapshots of agency {AgencyId} not computed.")]
    private partial void LogAgencyCrashed(Exception exception, Guid agencyId);
}
