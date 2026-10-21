using CarRental.Application.Pricing.Contracts;
using CarRental.Domain.Common;

namespace CarRental.Application.Bookings.Contracts;

/// <summary>
/// Write API of the public funnel (phase 2) and of the WhatsApp bot's locked quotes: locked quotes, web requests
/// (create a draft, submit it, or both at once) and the "provide information" step of a request.
/// </summary>
/// <remarks>
/// <para>No permission is checked: the public endpoints call it for anonymous visitors. They run it in a DI scope whose
/// actor is a system actor (e.g. <c>system:public-funnel</c>), because the drivers and documents are registered first
/// through <c>ICustomerRegistration</c> (which requires <c>customers.manage</c>) and the payer is created here.</para>
/// <para>Prices always come from the pricing engine and availability is re-checked under the lock at the submission:
/// nothing carried by a deep link is trusted (SPEC §5). The blacklist is never checked here, so that nothing hints it
/// to an applicant: the agent's validation refuses silently.</para>
/// </remarks>
public interface IBookingRequests
{
    /// <summary>
    /// Prices <paramref name="request"/> and freezes it as a locked quote with a public code (<c>devis=Q7F3K2</c>), price
    /// guaranteed for 24 hours. Availability is not checked here (the bot checked it, the submission re-checks it).
    /// </summary>
    Task<Result<LockedQuoteInfo>> CreateLockedQuoteAsync(PriceRequest request, CancellationToken cancellationToken = default);

    /// <summary>The locked quote of a code (case ignored), expired or not; <see langword="null"/> when unknown.</summary>
    Task<LockedQuoteInfo?> GetLockedQuoteAsync(string code, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a draft request (<c>Draft</c>, numbered) from a locked quote — its price is kept while it is not expired —
    /// or from a price request priced now. Drivers can be given later, at the submission.
    /// </summary>
    Task<Result<BookingRequestResult>> CreateDraftAsync(BookingRequestDraft draft, CancellationToken cancellationToken = default);

    /// <summary>
    /// Submits a draft (<c>Draft</c> → <c>PendingValidation</c>) under the availability lock; the request holds the
    /// capacity for <c>bookings.hold_duration</c>. Records the WhatsApp notifications consent when given.
    /// </summary>
    Task<Result<BookingRequestResult>> SubmitAsync(Guid bookingId, BookingRequestParties parties, CancellationToken cancellationToken = default);

    /// <summary><see cref="CreateDraftAsync"/> and <see cref="SubmitAsync"/> in one transaction.</summary>
    Task<Result<BookingRequestResult>> SubmitRequestAsync(
        BookingRequestDraft draft,
        BookingRequestParties parties,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The customer completed a request on which an agent asked for information ("compléter mon dossier"):
    /// <c>InformationRequested</c> → <c>PendingValidation</c>, re-checked under the availability lock.
    /// </summary>
    Task<Result> ProvideInformationAsync(Guid bookingId, CancellationToken cancellationToken = default);
}

/// <summary>Write API for the Payments module: a validated request (fallback mode) was paid.</summary>
public interface IBookingPaymentStatus
{
    /// <summary><c>AwaitingPayment</c> → <c>Confirmed</c>, recording the payment reference in the timeline (idempotent on a confirmed file).</summary>
    Task<Result> MarkPaidAsync(Guid bookingId, string? paymentReference, CancellationToken cancellationToken = default);
}

/// <summary>
/// A web or WhatsApp request to create: from <see cref="LockedQuoteCode"/> or from <see cref="Price"/> (exactly one),
/// with its attribution (deep link <c>src</c> and UTM parameters) and an optional note of the customer.
/// <see cref="BookingId"/> lets the public funnel choose the id beforehand: the drivers' submissions are registered
/// for it before the request exists (ADR-0050).
/// </summary>
public sealed record BookingRequestDraft(
    SalesChannel Channel,
    string? LockedQuoteCode = null,
    PriceRequest? Price = null,
    RequestAttributionInfo? Attribution = null,
    string? Notes = null,
    Guid? BookingId = null);

/// <summary>Where a request came from: the <c>src</c> parameter of the deep link and the UTM parameters.</summary>
public sealed record RequestAttributionInfo(
    string? Source,
    string? UtmSource = null,
    string? UtmMedium = null,
    string? UtmCampaign = null,
    string? UtmTerm = null,
    string? UtmContent = null);

/// <summary>
/// The drivers of a request (persons registered through <c>ICustomerRegistration</c>) and its payer (the individual
/// customer of the main driver when <see langword="null"/>), and the WhatsApp notifications opt-in of the main driver.
/// </summary>
public sealed record BookingRequestParties(
    Guid MainDriverId,
    Guid? SecondaryDriverId = null,
    Guid? PayerCustomerId = null,
    bool WhatsAppNotificationsConsent = false);

/// <summary>The request created or submitted.</summary>
public sealed record BookingRequestResult(
    Guid BookingId,
    string Number,
    BookingStatusInfo Status,
    Money TotalInclVat,
    Money Deposit,
    DateTimeOffset? HoldExpiresAt);

/// <summary>A locked quote: what was priced, its total TTC and deposit, and the end of the price guarantee.</summary>
public sealed record LockedQuoteInfo(
    Guid QuoteId,
    string Code,
    Guid AgencyId,
    Guid CategoryId,
    SalesChannel Channel,
    Guid PickupLocationId,
    Guid ReturnLocationId,
    DateTimeOffset PickupAt,
    DateTimeOffset ReturnAt,
    int Days,
    Money TotalInclVat,
    Money Deposit,
    IReadOnlyList<SelectedExtra> Extras,
    string? PromoCode,
    Guid? GuaranteedVehicleId,
    DateTimeOffset ExpiresAtUtc,
    bool IsExpired);
