using System.Globalization;
using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Channels.Contracts;
using CarRental.Application.Common.Auditing;
using CarRental.Application.Common.BackgroundJobs;
using CarRental.Application.Common.Outbox;
using CarRental.Application.Common.Persistence;
using CarRental.Application.Referential.Contracts;
using CarRental.Domain.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CarRental.Application.Channels.Outbound;

/// <summary>
/// Safety net of the outbound synchronization, every minute: due retries, updates queued by another instance, price
/// changes. The in-process pump runs the same work right after the debounce window of a change.
/// </summary>
public sealed class ChannelSyncJob(ChannelSyncDispatcher dispatcher) : IRecurringJob
{
    internal const string ActorName = "system:channels-sync";

    public static string JobId => "channels.sync";

    public static string CronExpression => "* * * * *";

    public Task ExecuteAsync(CancellationToken cancellationToken) => dispatcher.RunAsync(cancellationToken);
}

/// <summary>
/// Full resynchronization of every connected channel of the agencies whose channels module is on, every night at 02:40
/// (Tunis): availability and prices of every mapped category over the horizon, whatever an outage or a lost signal may
/// have missed.
/// </summary>
public sealed class ChannelFullResyncJob(ChannelSyncDispatcher dispatcher) : IRecurringJob
{
    public static string JobId => "channels.full_resync";

    public static string CronExpression => "40 2 * * *";

    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        await dispatcher.EnqueueFullResyncAsync("nightly", cancellationToken);
        await dispatcher.RunAsync(cancellationToken);
    }
}

/// <summary>
/// Retention of the message log of the channels: every night at 03:50 (Tunis), deletes the rows older than
/// <c>channels.message_log_retention_days</c> (one value for the platform) in batches, agency by agency — whether its
/// channels module is on or off — then the platform's own rows (messages to no known channel), and writes each count to
/// the audit log of its agency (of the platform for its rows).
/// </summary>
internal sealed partial class ChannelMessageLogRetentionJob(
    IAppDbContext db,
    ISettingsReader settings,
    IAgencyDirectory agencies,
    IAuditTrail auditTrail,
    TimeProvider timeProvider,
    ILogger<ChannelMessageLogRetentionJob> logger) : IRecurringJob
{
    public const int BatchSize = 5_000;
    public const string PurgedAction = "channels.message_log_purged";
    private const int MaxBatchesPerRun = 200;

    public static string JobId => "channels.purge_message_log";

    public static string CronExpression => "50 3 * * *";

    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        var days = await settings.GetAsync(ChannelsSettings.MessageLogRetentionDays, cancellationToken: cancellationToken);
        var cutoff = timeProvider.GetUtcNow() - TimeSpan.FromDays(days);
        var known = (await agencies.ListAsync(cancellationToken)).Select(a => a.Id).ToList();
        foreach (var agencyId in known)
        {
            var deleted = await DeleteInBatchesAsync(db.Set<ChannelMessageLog>().Where(m => m.AgencyId == agencyId && m.OccurredAtUtc < cutoff), cancellationToken);
            await RecordAsync(agencyId, deleted, days, cancellationToken);
        }

        // Messages to no known channel, and those of an agency the directory no longer lists (deleted): the platform's.
        var others = await DeleteInBatchesAsync(
            db.Set<ChannelMessageLog>().Where(m => (m.AgencyId == null || !known.Contains(m.AgencyId.Value)) && m.OccurredAtUtc < cutoff),
            cancellationToken);
        await RecordAsync(null, others, days, cancellationToken);
    }

    private static async Task<int> DeleteInBatchesAsync(IQueryable<ChannelMessageLog> query, CancellationToken cancellationToken)
    {
        var total = 0;
        for (var batch = 0; batch < MaxBatchesPerRun; batch++)
        {
            var deleted = await query.Take(BatchSize).ExecuteDeleteAsync(cancellationToken);
            total += deleted;
            if (deleted < BatchSize)
            {
                break;
            }
        }

        return total;
    }

    private async Task RecordAsync(Guid? agencyId, int total, int days, CancellationToken cancellationToken)
    {
        if (total == 0)
        {
            return;
        }

        await auditTrail.RecordAsync(
            new AuditEvent(PurgedAction)
            {
                EntityType = nameof(ChannelMessageLog),
                Details = string.Create(CultureInfo.InvariantCulture, $"{total} channel message(s) older than {days} days"),
                AgencyId = agencyId,
            },
            cancellationToken);
        LogPurged(total, days);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "{Count} channel message(s) purged after {Days} days of retention.")]
    private partial void LogPurged(int count, int days);
}

/// <summary>
/// Keeps the channel bookings in step with their rental files (<see cref="BookingStatusChanged"/>, idempotent): a file
/// cancelled at the counter cancels its channel booking (no commission), a no-show is recorded for the reconciliation.
/// Bookkeeping of the agency's own figures, done whether its channels module is on or off (nothing is sent to the channel).
/// </summary>
internal sealed class ChannelBookingStatusMirror(IAppDbContext db, TimeProvider clock) : IOutboxHandler<BookingStatusChanged>
{
    public async Task HandleAsync(BookingStatusChanged message, OutboxMessageContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        var state = message.Status switch
        {
            BookingStatusInfo.Cancelled or BookingStatusInfo.Refused or BookingStatusInfo.Expired => ChannelBookingState.Cancelled,
            BookingStatusInfo.NoShow => ChannelBookingState.NoShow,
            _ => (ChannelBookingState?)null,
        };
        if (state is null || !Channel.ExternalSalesChannels.Contains(message.Channel))
        {
            return;
        }

        var record = await db.Set<ChannelBooking>().FirstOrDefaultAsync(b => b.BookingId == message.BookingId && b.AgencyId == message.AgencyId, cancellationToken);
        if (record is null || record.State == state)
        {
            return;
        }

        record.Mirror(state.Value, clock.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);
    }
}
