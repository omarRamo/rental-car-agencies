using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Common.Email;

namespace CarRental.Application.Bookings.Notifications;

/// <summary>Who an e-mail to a customer goes to.</summary>
public sealed record CustomerRecipient(string Email, string? Name);

/// <summary>The access e-mail of "Ma réservation": a single-use link and a six-digit code, valid until <paramref name="ExpiresAtUtc"/>.</summary>
public sealed record AccessEmail(string Number, string Culture, string LinkPath, string Code, DateTimeOffset ExpiresAtUtc, string? BaseUrl);

/// <summary>
/// Outbox message of an access request that matched a booking (ADR-0051): the access e-mail is composed and queued out
/// of the request, which then does the same work whether a booking matched or not. It holds the link and the code in
/// clear, as the e-mail it becomes does (outbox payloads are never shown).
/// </summary>
public sealed record AccessEmailRequested(AccessEmail Email, CustomerRecipient Recipient);

/// <summary>
/// The e-mails to customers (ADR-0051), written by the web host with its templates (Razor components rendered to
/// HTML, with a text alternative) and resources, in the language of the customer. Called inside the operation that
/// queues them (outbox): nothing is sent from here.
/// </summary>
public interface ICustomerEmailComposer
{
    /// <summary>The e-mail of a notification (request received, information requested, confirmed, refused, expired, payment link, cancellation…).</summary>
    Task<EmailMessage> ComposeNotificationAsync(BookingCustomerNotification notification, CustomerRecipient recipient, CancellationToken cancellationToken = default);

    /// <summary>The access e-mail of "Ma réservation" (link and code).</summary>
    Task<EmailMessage> ComposeAccessAsync(AccessEmail email, CustomerRecipient recipient, CancellationToken cancellationToken = default);
}
