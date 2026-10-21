using CarRental.Application.Bookings.Administration;
using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Pricing.Contracts;
using CarRental.Domain.Common;

namespace CarRental.Application.Bookings.PublicAccess;

/// <summary>
/// The choice of a category on the results page, after the search was validated again by the site:
/// <c>SearchQuery</c> is the canonical query of the search (a new search after the expiry), <c>QuoteCode</c> the
/// <c>devis</c> code of a deep link, reused when valid, unexpired and matching the search.
/// </summary>
public sealed record FunnelStartRequest(
    Guid AgencyId,
    string Culture,
    SalesChannel Channel,
    RequestAttributionInfo? Attribution,
    Guid CategoryId,
    string CategoryCode,
    Guid PickupLocationId,
    Guid ReturnLocationId,
    DateTimeOffset PickupAt,
    DateTimeOffset ReturnAt,
    string SearchQuery,
    string? QuoteCode);

/// <summary>A new funnel session: the token of its addresses (shown once, never stored).</summary>
public sealed record FunnelStarted(string Token, Guid SessionId, bool ReusedQuote);

/// <summary>Why a funnel address cannot be shown.</summary>
public enum FunnelLookupFailure
{
    /// <summary>Unknown or malformed token: back to the search.</summary>
    NotFound = 1,

    /// <summary>The price guarantee (and the session) expired: the same search again, with a message.</summary>
    Expired = 2,

    /// <summary>The request was sent: its confirmation page.</summary>
    Submitted = 3,
}

/// <summary>A funnel session as a page shows it, or why it cannot be shown (with what is needed to redirect).</summary>
public sealed record FunnelLookup(FunnelView? View, FunnelLookupFailure? Failure, string? Culture = null, string? SearchQuery = null, string? BookingNumber = null)
{
    public static FunnelLookup NotFound { get; } = new(null, FunnelLookupFailure.NotFound);
}

/// <summary>How the request is guaranteed at the submission.</summary>
public enum FunnelGuaranteeMode
{
    /// <summary>Pre-authorization of <see cref="FunnelGuarantee.Amount"/> on the payment page of the bank, captured at the validation.</summary>
    PreAuthorization = 1,

    /// <summary>No pre-authorization (fallback mode or gateway unavailable): a payment link is sent after the validation.</summary>
    PaymentLinkAfterValidation = 2,
}

/// <summary>The guarantee of the request: mode and amount (total TTC × <c>bookings.online_guarantee_ratio</c>).</summary>
public sealed record FunnelGuarantee(FunnelGuaranteeMode Mode, Money Amount);

/// <summary>A selectable extra offered by the plan, with the quantity chosen (0: not chosen) and the maximum offered.</summary>
public sealed record FunnelExtraView(
    string Code,
    string Name,
    string? NameEn,
    ExtraKindInfo Kind,
    ExtraPricingModeInfo Mode,
    Money Price,
    Money? CapPerUnit,
    int Quantity,
    int MaxQuantity);

/// <summary>A photo received for a slot (the last one counts); the image itself is never sent back.</summary>
public sealed record FunnelUploadView(string Slot, Guid UploadId, string ContentType, DateTimeOffset UploadedAtUtc);

/// <summary>Everything a step of the funnel shows.</summary>
public sealed record FunnelView(
    Guid SessionId,
    Guid AgencyId,
    string Culture,
    SalesChannel Channel,
    Guid CategoryId,
    string CategoryCode,
    Guid PickupLocationId,
    Guid ReturnLocationId,
    DateTimeOffset PickupAt,
    DateTimeOffset ReturnAt,
    string SearchQuery,
    string QuoteCode,
    DateTimeOffset ExpiresAtUtc,
    QuoteView Quote,
    Money DailyContribution,
    Money StandardDeposit,
    Money ReducedDeposit,
    IReadOnlyList<FunnelExtraView> Extras,
    FunnelDriversInput? Drivers,
    FunnelDriversDraft? Draft,
    IReadOnlyList<FunnelUploadView> Uploads,
    bool DocumentProcessingConsent,
    bool WhatsAppNotificationsConsent,
    bool MarketingConsent,
    FunnelGuarantee Guarantee,
    string? Notice,
    bool FirstVisit,
    RequestAttributionInfo? Attribution = null)
{
    /// <summary>Total TTC divided by the charged days, to the millime.</summary>
    public Money PerDay => Money.Tnd(Quote.TotalInclVat.Amount / Math.Max(1, Quote.Days));

    public bool HasDrivers => Drivers is not null;
}

/// <summary>A driver as typed in step 2 (raw form values; dates as <c>yyyy-MM-dd</c>).</summary>
public sealed record FunnelDriverInput(
    string? FirstName = null,
    string? LastName = null,
    string? Nationality = null,
    string? BirthDate = null,
    string? Phone = null,
    string? Email = null,
    string? LicenseNumber = null,
    string? LicenseIssuedOn = null,
    string? LicenseExpiresOn = null,
    string? LicenseCountry = null,
    bool LicenseLatinScript = true,
    string? IdentityNumber = null,
    string? IdentityCountry = null,
    string? IdentityExpiresOn = null,
    string? PermitNumber = null,
    string? PermitCountry = null,
    string? PermitExpiresOn = null);

/// <summary>Step 2 as typed: the main driver, the optional second driver and the consents.</summary>
public sealed record FunnelDriversInput(
    FunnelDriverInput Main,
    bool HasSecond,
    FunnelDriverInput? Second,
    bool DocumentProcessingConsent,
    bool WhatsAppNotificationsConsent,
    bool MarketingConsent);

/// <summary>
/// An error of a field of the funnel: <see cref="Field"/> is <c>{driver}.{field}</c> (<c>main.birth_date</c>,
/// <c>second.eligibility</c>, <c>consent.documents</c>…), <see cref="Code"/> the text key (a stable error code or
/// <c>funnel.*</c>), <see cref="Arguments"/> invariant values for the text.
/// </summary>
public sealed record FunnelFieldError(string Field, string Code, IReadOnlyList<string>? Arguments = null);

/// <summary>The last form of step 2 sent with errors, shown again once.</summary>
public sealed record FunnelDriversDraft(FunnelDriversInput Input, IReadOnlyList<FunnelFieldError> Errors);

/// <summary>Outcome of step 2: valid (saved, possibly re-quoted) or the errors to show.</summary>
public sealed record FunnelDriversOutcome(bool IsValid, IReadOnlyList<FunnelFieldError> Errors);

/// <summary>A photo received for a slot.</summary>
public sealed record FunnelUploadResult(Guid UploadId, string Slot, string ContentType, long Length);

/// <summary>The request created by the submission, and how it is guaranteed.</summary>
public sealed record FunnelSubmitted(Guid BookingId, string Number, string Culture, FunnelGuarantee Guarantee);

/// <summary>Where the guarantee of a request stands, for its confirmation page.</summary>
public enum FunnelPaymentState
{
    /// <summary>No pre-authorization asked (payment link after the validation).</summary>
    None = 0,

    /// <summary>A payment page was opened; nothing confirmed by the bank yet (the customer may still be paying).</summary>
    AwaitingCustomer = 1,

    /// <summary>The bank holds the amount (pre-authorized).</summary>
    Held = 2,

    /// <summary>The payment failed, was abandoned or expired: a new attempt can be made.</summary>
    Failed = 3,

    /// <summary>Taken (validated request).</summary>
    Captured = 4,
}

/// <summary>A display of the confirmation of a request: measured once, with the category and the source of the funnel.</summary>
public sealed record FunnelConfirmationVisit(bool FirstVisit, string CategoryCode, RequestAttributionInfo? Attribution);

/// <summary>The confirmation page of a request.</summary>
public sealed record FunnelConfirmation(
    Guid BookingId,
    string Number,
    BookingStatusInfo Status,
    Money TotalInclVat,
    FunnelGuarantee Guarantee,
    FunnelPaymentState Payment,
    bool CanRetryPayment,
    string Culture);

/// <summary>Errors of a public page, for the endpoints: the field errors of step 2 or a single error code.</summary>
public static class FunnelFields
{
    public const string Main = "main";
    public const string Second = "second";

    /// <summary>Field of the consent to the processing of the identity documents.</summary>
    public const string DocumentConsent = "consent.documents";

    public const string Eligibility = "eligibility";

    /// <summary>The code of the extra linked to the second driver when the plan offers it (convention of the demo plan).</summary>
    public const string AdditionalDriverExtra = "ADDITIONAL_DRIVER";
}
