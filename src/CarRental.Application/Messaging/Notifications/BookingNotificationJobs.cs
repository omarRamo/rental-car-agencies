using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Common.BackgroundJobs;
using CarRental.Application.Common.Outbox;
using CarRental.Domain.Common;
using CarRental.Domain.Messaging;
using Microsoft.Extensions.Logging;

namespace CarRental.Application.Messaging.Notifications;

/// <summary>
/// Notifications triggered by a status change of a rental file (contract message of the Bookings module): the
/// confirmation when a file becomes <c>Confirmed</c> (validated request, confirmed option, paid file, booking confirmed
/// at creation) and the payment notice when a validated request waits for its payment (<c>AwaitingPayment</c>) — for an
/// agency whose WhatsApp module is on only, in a scope working for the agency of the file.
/// </summary>
/// <remarks>
/// TODO(spec): the payment link itself (Clic to Pay <c>formUrl</c>, package 2B) is not exposed by a contract yet; the
/// payment template carries the amount and the deadline, and the link is sent by e-mail until a URL button parameter
/// is added (ADR-0048).
/// </remarks>
internal sealed class BookingStatusNotificationHandler(MessagingScopes scopes) : IOutboxHandler<BookingStatusChanged>
{
    public async Task HandleAsync(BookingStatusChanged message, OutboxMessageContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(context);
        NotificationKind? kind = message.Status switch
        {
            BookingStatusInfo.Confirmed when message.PreviousStatus != BookingStatusInfo.Confirmed => NotificationKind.BookingConfirmed,
            BookingStatusInfo.AwaitingPayment when message.PreviousStatus != BookingStatusInfo.AwaitingPayment => NotificationKind.PaymentLink,
            _ => null,
        };
        if (kind is { } notification && message.AgencyId != Guid.Empty && await scopes.IsOpenAsync(message.AgencyId, cancellationToken))
        {
            await scopes.RunAsync<BookingNotifier, NotificationOutcome>(
                (notifier, ct) => notifier.NotifyAsync(message.BookingId, notification, ct),
                context.CorrelationId,
                cancellationToken,
                message.AgencyId);
        }
    }
}

/// <summary>
/// The rental files due for a dated notification: departures of tomorrow (confirmed), returns of tomorrow (rentals in
/// progress) and returns of yesterday (returned or closed), per active agency whose WhatsApp module is on, in Tunis days.
/// Each file is notified in a scope of its own, working for its agency: one failure does not stop the others, and the
/// next hourly run tries again.
/// </summary>
internal sealed partial class BookingReminderSweep(
    IBookingsQueries bookings,
    MessagingScopes scopes,
    TimeProvider clock,
    ILogger<BookingReminderSweep> logger)
{
    public async Task<int> RunAsync(NotificationKind kind, CancellationToken cancellationToken)
    {
        var today = TunisTime.Today(clock);
        var queued = 0;
        foreach (var agencyId in await scopes.OpenAgenciesAsync(cancellationToken))
        {
            foreach (var bookingId in await DueAsync(kind, agencyId, today, cancellationToken))
            {
                try
                {
                    var outcome = await scopes.RunAsync<BookingNotifier, NotificationOutcome>(
                        (notifier, ct) => notifier.NotifyAsync(bookingId, kind, ct),
                        correlationId: null,
                        cancellationToken,
                        agencyId);
                    queued += outcome == NotificationOutcome.Queued ? 1 : 0;
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    LogFailed(exception, kind.ToString());
                }
            }
        }

        return queued;
    }

    private async Task<IReadOnlyList<Guid>> DueAsync(NotificationKind kind, Guid agencyId, DateOnly today, CancellationToken cancellationToken)
    {
        switch (kind)
        {
            case NotificationKind.PickupReminder:
                return [.. (await bookings.GetDayAgendaAsync(agencyId, today.AddDays(1), cancellationToken)).Departures
                    .Where(d => d.Status == BookingStatusInfo.Confirmed)
                    .Select(d => d.BookingId)];
            case NotificationKind.ReturnReminder:
                return [.. (await bookings.GetDayAgendaAsync(agencyId, today.AddDays(1), cancellationToken)).Returns
                    .Where(r => r.Status == BookingStatusInfo.InProgress)
                    .Select(r => r.BookingId)];
            case NotificationKind.ReviewRequest:
                return [.. (await bookings.GetDayAgendaAsync(agencyId, today.AddDays(-1), cancellationToken)).Returns
                    .Where(r => r.Status is BookingStatusInfo.Returned or BookingStatusInfo.Closed)
                    .Select(r => r.BookingId)];
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, "Only the dated notifications are swept.");
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "WhatsApp {Kind} of a rental file failed; the next run tries again.")]
    private partial void LogFailed(Exception exception, string kind);
}

/// <summary>Reminder the day before the pick-up, with the address (hourly from 09:00 to 19:00, Tunis time).</summary>
internal sealed class PickupReminderJob(BookingReminderSweep sweep) : IRecurringJob
{
    public static string JobId => "messaging.pickup_reminders";

    public static string CronExpression => "0 9-19 * * *";

    public Task ExecuteAsync(CancellationToken cancellationToken) => sweep.RunAsync(NotificationKind.PickupReminder, cancellationToken);
}

/// <summary>Reminder the day before the return (hourly from 09:00 to 19:00, Tunis time).</summary>
internal sealed class ReturnReminderJob(BookingReminderSweep sweep) : IRecurringJob
{
    public static string JobId => "messaging.return_reminders";

    public static string CronExpression => "15 9-19 * * *";

    public Task ExecuteAsync(CancellationToken cancellationToken) => sweep.RunAsync(NotificationKind.ReturnReminder, cancellationToken);
}

/// <summary>Review request the day after the return (hourly from 10:00 to 19:00, Tunis time).</summary>
internal sealed class ReviewRequestJob(BookingReminderSweep sweep) : IRecurringJob
{
    public static string JobId => "messaging.review_requests";

    public static string CronExpression => "30 10-19 * * *";

    public Task ExecuteAsync(CancellationToken cancellationToken) => sweep.RunAsync(NotificationKind.ReviewRequest, cancellationToken);
}
