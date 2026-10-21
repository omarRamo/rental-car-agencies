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
/// Full resynchronization of every connected channel, every night at 02:40 (Tunis): availability and prices of every
/// mapped category over the horizon, whatever an outage or a lost signal may have missed.
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
/// <c>channels.message_log_retention_days</c> in batches, and writes the count to the audit log.
/// </summary>
internal sealed partial class ChannelMessageLogRetentionJob(
    IAppDbContext db,
    ISettingsReader settings,
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
        var total = 0;
        for (var batch = 0; batch < MaxBatchesPerRun; batch++)
        {
            var deleted = await db.Set<ChannelMessageLog>().Where(m => m.OccurredAtUtc < cutoff).Take(BatchSize).ExecuteDeleteAsync(cancellationToken);
            total += deleted;
            if (deleted < BatchSize)
            {
                break;
            }
        }

        if (total > 0)
        {
            await auditTrail.RecordAsync(
                new AuditEvent(PurgedAction)
                {
                    EntityType = nameof(ChannelMessageLog),
                    Details = string.Create(CultureInfo.InvariantCulture, $"{total} channel message(s) older than {days} days"),
                },
                cancellationToken);
        }

        LogPurged(total, days);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "{Count} channel message(s) purged after {Days} days of retention.")]
    private partial void LogPurged(int count, int days);
}

/// <summary>
/// Keeps the channel bookings in step with their rental files (<see cref="BookingStatusChanged"/>, idempotent): a file
/// cancelled at the counter cancels its channel booking (no commission), a no-show is recorded for the reconciliation.
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

        var record = await db.Set<ChannelBooking>().FirstOrDefaultAsync(b => b.BookingId == message.BookingId, cancellationToken);
        if (record is null || record.State == state)
        {
            return;
        }

        record.Mirror(state.Value, clock.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);
    }
}
