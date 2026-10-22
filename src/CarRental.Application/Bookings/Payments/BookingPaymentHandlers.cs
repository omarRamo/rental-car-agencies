using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Bookings.PublicAccess;
using CarRental.Application.Common.Outbox;
using CarRental.Application.Common.Persistence;
using CarRental.Application.Customers.Contracts;
using CarRental.Application.Payments.Contracts;
using CarRental.Application.Referential.Contracts;
using CarRental.Domain.Bookings;
using CarRental.Domain.Bookings.Access;
using CarRental.Domain.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CarRental.Application.Bookings.Payments;

/// <summary>
/// The end of a request (refused, expired, cancelled — ADR-0051): its pre-authorizations and unpaid payment pages are
/// released (<see cref="IOnlinePayments.ReverseBookingAsync"/>, idempotent: what cannot be confirmed now is completed
/// by the catch-up job of the Payments module), and the driver submissions still waiting for an agent are discarded
/// with their images (data minimization).
/// </summary>
internal sealed partial class BookingPaymentReleaseHandler(
    IOnlinePayments payments,
    ICustomerRegistration registration,
    ILogger<BookingPaymentReleaseHandler> logger) : IOutboxHandler<BookingStatusChanged>
{
    public async Task HandleAsync(BookingStatusChanged message, OutboxMessageContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.Status is not (BookingStatusInfo.Refused or BookingStatusInfo.Expired or BookingStatusInfo.Cancelled))
        {
            return;
        }

        if (payments.IsAvailable)
        {
            var released = await payments.ReverseBookingAsync(message.BookingId, cancellationToken);
            if (released.IsFailure)
            {
                // Throws so that the outbox retries later: a held amount must not stay on the customer's card.
                throw new InvalidOperationException($"The online payments of booking {message.Number} could not be released ({released.Error.Code}).");
            }
        }

        var discarded = await registration.DiscardDriverSubmissionsAsync(message.BookingId, cancellationToken);
        if (discarded.IsFailure)
        {
            LogDiscardFailed(message.Number, discarded.Error.Code);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The pending driver submissions of booking {Number} could not be discarded ({ErrorCode}).")]
    private partial void LogDiscardFailed(string number, string errorCode);
}

/// <summary>
/// The payment link mode (<c>bookings.validation_payment_mode</c> or a request validated without guarantee): when a
/// request becomes <c>AwaitingPayment</c>, a payment link (<c>register.do</c>, valid until the payment deadline) is
/// created for the balance and the customer is notified. A link still open for the booking is reused (message
/// delivered again). Without online payment, nothing is sent: the agency collects the payment itself.
/// </summary>
internal sealed partial class BookingPaymentLinkHandler(
    IAppDbContext db,
    IOnlinePayments payments,
    IPaymentsQueries paymentQueries,
    IOutbox outbox,
    IAgencyDirectory agencies,
    TimeProvider clock,
    ILogger<BookingPaymentLinkHandler> logger) : IOutboxHandler<BookingStatusChanged>
{
    /// <summary>A link must leave the customer some time to pay.</summary>
    private static readonly TimeSpan MinimumValidity = TimeSpan.FromMinutes(15);

    public async Task HandleAsync(BookingStatusChanged message, OutboxMessageContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.Status != BookingStatusInfo.AwaitingPayment)
        {
            return;
        }

        var booking = await db.Set<Booking>().AsNoTracking().FirstOrDefaultAsync(b => b.Id == message.BookingId, cancellationToken);
        var now = clock.GetUtcNow();
        if (booking is not { Status: BookingStatus.AwaitingPayment, PaymentDueAt: { } due } || due - now < MinimumValidity)
        {
            return;
        }

        if (!payments.IsAvailable)
        {
            LogNoGateway(booking.Number);
            return;
        }

        var paid = (await paymentQueries.GetBookingSummaryAsync(booking.Id, cancellationToken)).Payments.NetPaid;
        var amount = booking.TotalInclVat - paid;
        if (!amount.IsPositive)
        {
            return;
        }

        var profile = await db.Set<BookingCustomerProfile>().AsNoTracking().FirstOrDefaultAsync(p => p.BookingId == booking.Id, cancellationToken);
        var culture = profile?.Culture ?? "fr";
        var open = (await payments.GetBookingOnlinePaymentsAsync(booking.Id, cancellationToken))
            .LastOrDefault(p => p.IsPaymentLink && p.Status == PaymentStatusInfo.Pending && p.PaymentPageUrl is not null && p.ExpiresAtUtc > now);
        string url;
        DateTimeOffset expires;
        if (open is not null)
        {
            url = open.PaymentPageUrl!;
            expires = open.ExpiresAtUtc!.Value;
        }
        else
        {
            var path = PublicBookingPaths.PaymentResult(await agencies.SlugAsync(booking.AgencyId, cancellationToken), culture);
            var started = await payments.StartPaymentAsync(
                new OnlinePaymentRequest(booking.Id, booking.AgencyId, amount.Amount, culture, path, path, booking.Number, IsLink: true, Validity: due - now),
                cancellationToken);
            if (started.IsFailure)
            {
                // Retried by the outbox: the customer has a deadline to pay.
                throw new InvalidOperationException($"The payment link of booking {booking.Number} could not be created ({started.Error.Code}).");
            }

            url = started.Value.RedirectUrl;
            expires = started.Value.ExpiresAtUtc;
        }

        if (profile is null)
        {
            return;
        }

        outbox.Enqueue(new BookingCustomerNotification(
            Guid.CreateVersion7(),
            booking.Id,
            booking.AgencyId,
            booking.Number,
            BookingNotificationKind.PaymentLink,
            culture,
            now,
            ActionUrl: url,
            ActionExpiresAtUtc: expires,
            Amount: amount,
            BaseUrl: profile.PublicBaseUrl));
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Booking {Number} waits for its payment but online payment is off: the agency collects it.")]
    private partial void LogNoGateway(string number);
}

/// <summary>
/// The confirmed changes of the online payments of a booking (contract message of the Payments module, ADR-0051):
/// the payment state kept for the agent checklist and "Ma réservation" is recomputed from all its payments (so the
/// order of delivery does not matter), a payment made after its expiry is flagged, and a captured link or balance
/// confirms a booking waiting for its payment (<see cref="IBookingPaymentStatus.MarkPaidAsync"/>, idempotent).
/// </summary>
internal sealed partial class BookingOnlinePaymentHandler(
    IAppDbContext db,
    IOnlinePayments payments,
    IBookingPaymentStatus paymentStatus,
    TimeProvider clock,
    ILogger<BookingOnlinePaymentHandler> logger) : IOutboxHandler<OnlinePaymentStatusChanged>
{
    public async Task HandleAsync(OnlinePaymentStatusChanged message, OutboxMessageContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        var booking = await db.Set<Booking>()
            .AsNoTracking()
            .Where(b => b.Id == message.BookingId)
            .Select(b => new { b.Id, b.AgencyId, b.Number, b.Status })
            .FirstOrDefaultAsync(cancellationToken);
        if (booking is null)
        {
            return;
        }

        var all = await payments.GetBookingOnlinePaymentsAsync(booking.Id, cancellationToken);
        var (state, paymentId, amount) = Summarize(all);
        var track = await db.Set<BookingPaymentTrack>().FirstOrDefaultAsync(t => t.BookingId == booking.Id, cancellationToken);
        if (track is null)
        {
            track = BookingPaymentTrack.Create(booking.AgencyId, booking.Id, clock.GetUtcNow());
            db.Set<BookingPaymentTrack>().Add(track);
        }

        track.Apply(state, paymentId, amount, message.PaidLate, clock.GetUtcNow());
        if (message.PaidLate)
        {
            LogPaidLate(booking.Number);
        }

        if (message.Status == PaymentStatusInfo.Captured && message.Flow == OnlinePaymentFlowInfo.OneStep && booking.Status == BookingStatus.AwaitingPayment)
        {
            var paid = await paymentStatus.MarkPaidAsync(booking.Id, message.PaymentId.ToString("N")[^8..].ToUpperInvariant(), cancellationToken);
            if (paid.IsFailure)
            {
                LogMarkPaidFailed(booking.Number, paid.Error.Code);
            }
        }
    }

    /// <summary>
    /// The state of a booking from its online payments: captured (or refunded) first, then held, then the newest attempt
    /// (waiting for the customer, failed, expired or released).
    /// </summary>
    internal static (RequestPaymentState State, Guid? PaymentId, Money? Amount) Summarize(IReadOnlyList<OnlinePaymentInfo> payments)
    {
        ArgumentNullException.ThrowIfNull(payments);
        if (payments.LastOrDefault(p => p.Status == PaymentStatusInfo.Captured) is { } captured)
        {
            return (RequestPaymentState.Captured, captured.PaymentId, captured.CapturedAmount);
        }

        if (payments.LastOrDefault(p => p.Status == PaymentStatusInfo.PreAuthorized) is { } held)
        {
            return (RequestPaymentState.PreAuthorized, held.PaymentId, held.Amount);
        }

        if (payments.LastOrDefault(p => p.Status == PaymentStatusInfo.Refunded) is { } refunded)
        {
            return (RequestPaymentState.Refunded, refunded.PaymentId, refunded.CapturedAmount);
        }

        var latest = payments.Count == 0 ? null : payments[^1];
        return latest switch
        {
            null => (RequestPaymentState.None, null, null),
            { Status: PaymentStatusInfo.Pending } => (RequestPaymentState.AwaitingCustomer, latest.PaymentId, latest.Amount),
            { Status: PaymentStatusInfo.Failed } => (RequestPaymentState.Failed, latest.PaymentId, latest.Amount),
            { Status: PaymentStatusInfo.Cancelled, ExpiredAtUtc: not null } => (RequestPaymentState.Expired, latest.PaymentId, latest.Amount),
            _ => (RequestPaymentState.Released, latest.PaymentId, latest.Amount),
        };
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Booking {Number}: a payment was made after its attempt had expired (to check).")]
    private partial void LogPaidLate(string number);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Booking {Number} was paid online but could not be marked paid ({ErrorCode}).")]
    private partial void LogMarkPaidFailed(string number, string errorCode);
}
