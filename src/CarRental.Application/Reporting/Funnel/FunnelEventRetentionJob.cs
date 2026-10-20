using System.Globalization;
using CarRental.Application.Common.Auditing;
using CarRental.Application.Common.BackgroundJobs;
using CarRental.Application.Common.Persistence;
using CarRental.Application.Referential.Contracts;
using CarRental.Application.Reporting.Contracts;
using CarRental.Domain.Reporting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CarRental.Application.Reporting.Funnel;

/// <summary>
/// Retention of the funnel measurement: every night, deletes the funnel events older than
/// <c>reporting.funnel_event_retention_days</c> (400 by default), in batches so that a long interruption does not turn
/// into one huge statement, and writes the count to the audit log.
/// </summary>
internal sealed partial class FunnelEventRetentionJob(
    IAppDbContext db,
    ISettingsReader settings,
    IAuditTrail auditTrail,
    TimeProvider timeProvider,
    ILogger<FunnelEventRetentionJob> logger) : IRecurringJob
{
    public const int BatchSize = 5_000;
    private const int MaxBatchesPerRun = 200;

    public static string JobId => "reporting.purge_funnel_events";

    /// <summary>Every night at 03:45, Tunis time.</summary>
    public static string CronExpression => "45 3 * * *";

    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        var days = await settings.GetAsync(ReportingSettings.FunnelEventRetentionDays, cancellationToken: cancellationToken);
        var cutoff = timeProvider.GetUtcNow() - TimeSpan.FromDays(days);

        var total = 0;
        for (var batch = 0; batch < MaxBatchesPerRun; batch++)
        {
            var deleted = await db.Set<FunnelEvent>()
                .Where(e => e.OccurredAtUtc < cutoff)
                .Take(BatchSize)
                .ExecuteDeleteAsync(cancellationToken);
            total += deleted;
            if (deleted < BatchSize)
            {
                break;
            }
        }

        if (total > 0)
        {
            await auditTrail.RecordAsync(
                new AuditEvent(ReportingAuditActions.FunnelEventsPurged)
                {
                    EntityType = nameof(FunnelEvent),
                    Details = string.Create(CultureInfo.InvariantCulture, $"{total} funnel event(s) older than {days} days"),
                },
                cancellationToken);
        }

        LogPurged(total, days);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "{Count} funnel event(s) purged after {Days} days of retention.")]
    private partial void LogPurged(int count, int days);
}
