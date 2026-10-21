using CarRental.Application.Referential.Contracts;
using CarRental.Domain.Common;

namespace CarRental.Application.Bookings.Contracts;

/// <summary>
/// Settings of the public booking funnel and of "Ma réservation" (ADR-0049, ADR-0051), declared by
/// <c>AddBookingsModule()</c>.
/// </summary>
public static class PublicBookingSettings
{
    /// <summary>
    /// Part of the total TTC pre-authorized at the submission of a web request (1 = the whole total, SPEC §9 default).
    /// TODO(spec): guarantee policy of the agency (deposit or whole amount, by season and by channel).
    /// </summary>
    public static readonly SettingDefinition<decimal> GuaranteeRatio =
        SettingDefinitions.Number("bookings.online_guarantee_ratio", 1m, 0.01m, 1m);

    /// <summary>Validity of the link and of the six-digit code sent on request by "Ma réservation" (30 minutes).</summary>
    public static readonly SettingDefinition<TimeSpan> AccessLinkValidity =
        SettingDefinitions.Duration("bookings.my_booking_link_validity", TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(5), TimeSpan.FromHours(2)).PlatformOnly();

    /// <summary>Validity of a "compléter mon dossier" link (information requested, or sent by an agent): 24 hours.</summary>
    public static readonly SettingDefinition<TimeSpan> CompleteFileLinkValidity =
        SettingDefinitions.Duration("bookings.complete_file_link_validity", TimeSpan.FromHours(24), TimeSpan.FromHours(1), TimeSpan.FromDays(3)).PlatformOnly();

    /// <summary>A session of "Ma réservation" ends after this time without activity (2 hours), and 24 hours after it opened in any case.</summary>
    public static readonly SettingDefinition<TimeSpan> SessionIdleTimeout =
        SettingDefinitions.Duration("bookings.my_booking_session_idle", TimeSpan.FromHours(2), TimeSpan.FromMinutes(10), TimeSpan.FromHours(24)).PlatformOnly();

    /// <summary>Wrong codes after which a link of "Ma réservation" can no longer be used (5).</summary>
    public static readonly SettingDefinition<int> CodeAttempts =
        SettingDefinitions.WholeNumber("bookings.my_booking_code_attempts", 5, 1, 10).PlatformOnly();

    /// <summary>Access e-mails sent at most for one booking per hour, whoever asks (5): no e-mail bombing.</summary>
    public static readonly SettingDefinition<int> AccessRequestsPerHour =
        SettingDefinitions.WholeNumber("bookings.my_booking_requests_per_hour", 5, 1, 50).PlatformOnly();

    public static IReadOnlyList<SettingDefinition> All { get; } =
    [
        GuaranteeRatio,
        AccessLinkValidity,
        CompleteFileLinkValidity,
        SessionIdleTimeout,
        CodeAttempts,
        AccessRequestsPerHour,
    ];
}

/// <summary>What a customer is told about a booking (e-mail now, WhatsApp with the phase 4).</summary>
public enum BookingNotificationKind
{
    /// <summary>A web or WhatsApp request was submitted: answer within two business hours.</summary>
    RequestReceived = 1,

    /// <summary>An agent asked for information: message and "compléter mon dossier" link.</summary>
    InformationRequested = 2,

    /// <summary>The request was validated (payment captured or received).</summary>
    Confirmed = 3,

    /// <summary>The request was refused (generic and polite, never the reason).</summary>
    Refused = 4,

    /// <summary>The request expired without an answer or its payment link was not paid.</summary>
    Expired = 5,

    /// <summary>Validated in the payment link mode: the link to pay before its deadline.</summary>
    PaymentLink = 6,

    /// <summary>The customer cancelled from "Ma réservation": fee and refund.</summary>
    CancellationConfirmed = 7,

    /// <summary>An agent sent the "compléter mon dossier" link (booking made by phone or at the counter).</summary>
    CompleteFileLink = 8,
}

/// <summary>What happened to the refund of a cancellation.</summary>
public enum CancellationRefundState
{
    /// <summary>Nothing to refund (nothing paid online, or the fee covers it).</summary>
    None = 0,

    /// <summary>Sent to the bank.</summary>
    Requested = 1,

    /// <summary>Above the approval threshold: waits for a manager.</summary>
    AwaitingApproval = 2,

    /// <summary>Paid otherwise than online, or the bank did not take it: the agency refunds it.</summary>
    ByTheAgency = 3,
}

/// <summary>
/// Outbox message for the customer notifications of a booking (ADR-0051): one per event, in the language of the
/// customer, handled by the e-mail sender of the web host and, from the phase 4, by the WhatsApp channel (with the
/// consent of the customer). Delivered at least once: a handler is idempotent on <see cref="NotificationId"/>.
/// </summary>
/// <param name="NotificationId">Unique id of the notification (idempotency key of the handlers).</param>
/// <param name="BookingId">The booking.</param>
/// <param name="AgencyId">Its agency.</param>
/// <param name="Number">Its readable number.</param>
/// <param name="Kind">What happened.</param>
/// <param name="Culture">Language of the customer (<c>fr</c>, <c>en</c>).</param>
/// <param name="OccurredAtUtc">When it happened.</param>
/// <param name="ActionPath">Local path of the action, e.g. a "compléter mon dossier" link (relative to the public base address).</param>
/// <param name="ActionUrl">Absolute address of an external action (payment page of the bank).</param>
/// <param name="ActionExpiresAtUtc">Deadline of the action (payment link, documents link).</param>
/// <param name="Amount">Amount concerned (payment link, refund).</param>
/// <param name="Fee">Cancellation fee kept.</param>
/// <param name="Refund">Refund of a cancellation.</param>
/// <param name="Message">The message of the agent to the customer (information requested).</param>
/// <param name="BaseUrl">The site address the customer used, when <c>PublicSite:BaseUrl</c> is not configured.</param>
public sealed record BookingCustomerNotification(
    Guid NotificationId,
    Guid BookingId,
    Guid AgencyId,
    string Number,
    BookingNotificationKind Kind,
    string Culture,
    DateTimeOffset OccurredAtUtc,
    string? ActionPath = null,
    string? ActionUrl = null,
    DateTimeOffset? ActionExpiresAtUtc = null,
    Money? Amount = null,
    Money? Fee = null,
    CancellationRefundState Refund = CancellationRefundState.None,
    string? Message = null,
    string? BaseUrl = null);

/// <summary>Kind of a document of a booking offered to the customer in "Ma réservation".</summary>
public enum CustomerDocumentKind
{
    /// <summary>The signed rental contract (package 1F-b).</summary>
    RentalContract = 1,

    /// <summary>An invoice (phase 3).</summary>
    Invoice = 2,

    /// <summary>A credit note (phase 3).</summary>
    CreditNote = 3,
}

/// <summary>A document of a booking the customer may download (contract, invoice): no content, only what to list.</summary>
/// <param name="Id">Identifier given back to <see cref="ICustomerDocumentProvider.OpenAsync"/> (opaque).</param>
/// <param name="Kind">Contract, invoice or credit note.</param>
/// <param name="Number">Its number when it has one (invoice <c>FAC-…</c>).</param>
/// <param name="IssuedAtUtc">When it was issued or signed.</param>
public sealed record CustomerDocumentInfo(string Id, CustomerDocumentKind Kind, string? Number, DateTimeOffset IssuedAtUtc);

/// <summary>An open document: dispose it once written.</summary>
public sealed class CustomerDocumentContent(Stream content, string contentType, string fileName) : IAsyncDisposable, IDisposable
{
    public Stream Content { get; } = content;

    /// <summary><c>application/pdf</c> in practice.</summary>
    public string ContentType { get; } = contentType;

    /// <summary>Neutral file name (no personal data), e.g. <c>contrat-LOC-2027-000123.pdf</c>.</summary>
    public string FileName { get; } = fileName;

    public ValueTask DisposeAsync() => Content.DisposeAsync();

    public void Dispose() => Content.Dispose();
}

/// <summary>
/// Extension point of "Ma réservation" for the documents of a booking (ADR-0051): the rental contract of the package
/// 1F-b and the invoices of the phase 3 register an implementation with
/// <c>services.AddScoped&lt;ICustomerDocumentProvider, X&gt;()</c>; until then the page says that no document is
/// available yet. Called for the booking of the customer's session only (already authorized by the caller), as the
/// system actor; an implementation lists only what the customer may see.
/// </summary>
public interface ICustomerDocumentProvider
{
    Task<IReadOnlyList<CustomerDocumentInfo>> GetDocumentsAsync(Guid bookingId, CancellationToken cancellationToken = default);

    /// <summary>The document <paramref name="documentId"/> of the booking, or <see langword="null"/> when it is not one of its documents.</summary>
    Task<CustomerDocumentContent?> OpenAsync(Guid bookingId, string documentId, CancellationToken cancellationToken = default);
}
