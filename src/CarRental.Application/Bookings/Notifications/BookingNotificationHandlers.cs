using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Common.Email;
using CarRental.Application.Common.Outbox;
using CarRental.Application.Common.Persistence;
using CarRental.Application.Customers.Contracts;
using CarRental.Application.Referential.Contracts;
using CarRental.Domain.Bookings;
using CarRental.Domain.Bookings.Access;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CarRental.Application.Bookings.Notifications;

/// <summary>
/// Turns the status changes of a booking made online (it has a customer profile) into customer notifications
/// (ADR-0051): request received, information requested (with a "compléter mon dossier" link opening the documents),
/// confirmed, refused, expired. The payment link of the fallback mode is told by <c>BookingPaymentLinkHandler</c> once
/// created, a cancellation from "Ma réservation" by that page (it knows the refund). The notification and the link are
/// written with the processed mark of the message: exactly once, even when the message is delivered again.
/// </summary>
internal sealed class BookingNotificationPublisher(IAppDbContext db, IOutbox outbox, ISettingsReader settings, TimeProvider clock)
    : IOutboxHandler<BookingStatusChanged>
{
    public async Task HandleAsync(BookingStatusChanged message, OutboxMessageContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (KindOf(message) is not { } kind)
        {
            return;
        }

        var profile = await db.Set<BookingCustomerProfile>().AsNoTracking().FirstOrDefaultAsync(p => p.BookingId == message.BookingId, cancellationToken);
        if (profile is null)
        {
            // Not a booking made online, and no link was ever sent: the agency talks to its customer itself.
            return;
        }

        string? actionPath = null;
        DateTimeOffset? actionExpires = null;
        string? agentMessage = null;
        if (kind == BookingNotificationKind.InformationRequested)
        {
            agentMessage = await db.Set<BookingEvent>()
                .AsNoTracking()
                .Where(e => e.BookingId == message.BookingId && e.Type == BookingEventType.InformationRequested)
                .OrderByDescending(e => e.Sequence)
                .Select(e => e.Reason)
                .FirstOrDefaultAsync(cancellationToken);
            var link = await AccessGrants.IssueCompleteFileLinkAsync(db, settings, message.BookingId, profile.Culture, clock.GetUtcNow(), cancellationToken);
            actionPath = PublicBookingPaths.AccessLink(profile.Culture, link.Token, documents: true);
            actionExpires = link.Grant.ExpiresAtUtc;
        }

        outbox.Enqueue(new BookingCustomerNotification(
            Guid.CreateVersion7(),
            message.BookingId,
            message.AgencyId,
            message.Number,
            kind,
            profile.Culture,
            message.OccurredAtUtc,
            ActionPath: actionPath,
            ActionExpiresAtUtc: actionExpires,
            Amount: kind == BookingNotificationKind.RequestReceived ? message.TotalInclVat : null,
            Message: agentMessage,
            BaseUrl: profile.PublicBaseUrl));
    }

    /// <summary>The notification of a status change of a request, if any.</summary>
    internal static BookingNotificationKind? KindOf(BookingStatusChanged message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return (message.PreviousStatus, message.Status) switch
        {
            (BookingStatusInfo.Draft, BookingStatusInfo.PendingValidation) => BookingNotificationKind.RequestReceived,
            (BookingStatusInfo.PendingValidation, BookingStatusInfo.InformationRequested) => BookingNotificationKind.InformationRequested,
            (BookingStatusInfo.PendingValidation or BookingStatusInfo.AwaitingPayment, BookingStatusInfo.Confirmed) => BookingNotificationKind.Confirmed,
            (_, BookingStatusInfo.Refused) => BookingNotificationKind.Refused,
            (BookingStatusInfo.PendingValidation or BookingStatusInfo.InformationRequested or BookingStatusInfo.AwaitingPayment, BookingStatusInfo.Expired) =>
                BookingNotificationKind.Expired,
            _ => null,
        };
    }
}

/// <summary>
/// E-mails the customer notifications (the WhatsApp channel of the phase 4 subscribes to the same message): to the
/// contact typed with the request (main driver), in its language, composed by the web host. The e-mail is queued with
/// the processed mark of the notification: exactly once.
/// </summary>
internal sealed partial class BookingNotificationEmailHandler(
    IAppDbContext db,
    ICustomersQueries customers,
    ICustomerEmailComposer composer,
    IOutbox outbox,
    ILogger<BookingNotificationEmailHandler> logger) : IOutboxHandler<BookingCustomerNotification>
{
    public async Task HandleAsync(BookingCustomerNotification message, OutboxMessageContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        var drivers = await db.Set<Booking>()
            .AsNoTracking()
            .Where(b => b.Id == message.BookingId)
            .Select(b => new { b.MainDriverId, b.PayerCustomerId })
            .FirstOrDefaultAsync(cancellationToken);
        CustomerRecipient? recipient = null;
        if (drivers?.MainDriverId is { } mainDriverId
            && await customers.GetBookingContactAsync(mainDriverId, message.BookingId, cancellationToken) is { Email: { Length: > 0 } email } contact)
        {
            recipient = new CustomerRecipient(email, contact.FullName);
        }
        else if (drivers?.PayerCustomerId is { } payerId && await customers.GetCustomerAsync(payerId, cancellationToken) is { Email: { Length: > 0 } payerEmail } payer)
        {
            recipient = new CustomerRecipient(payerEmail, payer.DisplayName);
        }

        if (recipient is null)
        {
            LogNoRecipient(message.Kind.ToString(), message.Number);
            return;
        }

        var composed = await composer.ComposeNotificationAsync(message, recipient, cancellationToken);
        outbox.Enqueue(new EmailRequested(composed));
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "No e-mail address for the {Kind} notification of booking {Number}: not sent.")]
    private partial void LogNoRecipient(string kind, string number);
}

/// <summary>Links of "Ma réservation" issued by the platform.</summary>
internal static class AccessGrants
{
    /// <summary>A "compléter mon dossier" link (no code), valid <c>bookings.complete_file_link_validity</c>, added to the unit of work.</summary>
    public static async Task<(BookingAccessGrant Grant, string Token)> IssueCompleteFileLinkAsync(
        IAppDbContext db,
        ISettingsReader settings,
        Guid bookingId,
        string culture,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var validity = await settings.GetAsync(PublicBookingSettings.CompleteFileLinkValidity, cancellationToken: cancellationToken);
        var token = AccessSecrets.NewToken();
        var grant = BookingAccessGrant.Issue(bookingId, AccessPurpose.CompleteFile, AccessSecrets.HashToken(token), null, null, culture, validity, now);
        db.Set<BookingAccessGrant>().Add(grant);
        return (grant, token);
    }
}
