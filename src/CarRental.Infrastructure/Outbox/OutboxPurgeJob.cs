using CarRental.Application.Common.BackgroundJobs;
using CarRental.Application.Referential.Contracts;
using CarRental.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CarRental.Infrastructure.Outbox;

/// <summary>
/// Every night at 02:30 (Tunis), deletes the processed outbox messages older than the setting
/// <c>outbox.processed_retention</c> (30 days by default), by batches. Pending and failed messages are never deleted:
/// a failed message stays until someone retries it from <c>/admin/outbox</c>. The table is excluded from the audit log.
/// </summary>
public sealed partial class OutboxPurgeJob(
    CarRentalDbContext db,
    ISettingsReader settings,
    TimeProvider timeProvider,
    ILogger<OutboxPurgeJob> logger) : IRecurringJob
{
    internal const int BatchSize = 1000;

    public static string JobId => "outbox.purge";

    public static string CronExpression => "30 2 * * *";

    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        var retention = await settings.GetAsync(WellKnownSettings.OutboxProcessedRetention, cancellationToken: cancellationToken);
        var cutoff = timeProvider.GetUtcNow() - retention;
        var total = 0;
        int deleted;
        do
        {
            // One statement per batch keeps the transaction log and the locks small.
            deleted = await db.Set<OutboxMessage>()
                .Where(m => m.ProcessedAtUtc != null && m.ProcessedAtUtc < cutoff)
                .Take(BatchSize)
                .ExecuteDeleteAsync(cancellationToken);
            total += deleted;
        }
        while (deleted == BatchSize);

        LogPurged(total, cutoff);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Outbox purge: {Count} processed message(s) before {Cutoff:O} deleted.")]
    private partial void LogPurged(int count, DateTimeOffset cutoff);
}
