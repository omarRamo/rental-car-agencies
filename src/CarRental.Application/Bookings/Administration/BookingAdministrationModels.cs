using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Customers.Contracts;
using CarRental.Application.Fleet.Contracts;
using CarRental.Application.Pricing.Contracts;
using CarRental.Application.Referential.Contracts;
using CarRental.Domain.Bookings;
using CarRental.Domain.Bookings.Access;
using CarRental.Domain.Bookings.Quotes;
using CarRental.Domain.Common;
using QuoteLineKind = CarRental.Domain.Bookings.Quotes.QuoteLineKind;

namespace CarRental.Application.Bookings.Administration;

/// <summary>A line of a quote version, as shown by the back office.</summary>
public sealed record QuoteLineView(
    int Position,
    QuoteLineKind Kind,
    string Code,
    string LabelKey,
    IReadOnlyList<string> LabelArguments,
    int Quantity,
    Money UnitPrice,
    Money ExclVat,
    Money Vat,
    Money InclVat,
    decimal VatRate);

/// <summary>A quote (version of a file, locked quote, or a price not saved yet such as an amendment preview).</summary>
public sealed record QuoteView(
    Guid? QuoteId,
    string? Code,
    int? Version,
    QuoteOrigin Origin,
    DateTimeOffset? CreatedAtUtc,
    string? CreatedBy,
    Guid RatePlanId,
    int? RatePlanVersion,
    int Days,
    DateTimeOffset PickupAt,
    DateTimeOffset ReturnAt,
    IReadOnlyList<QuoteLineView> Lines,
    Money TotalExclVat,
    Money TotalVat,
    Money TotalInclVat,
    Money Deposit,
    bool IncludesFranchiseWaiver,
    QuotePromoCodeOutcome PromoCodeOutcome,
    string? PromoCode,
    IReadOnlyList<AppliedPromotionInfo> Promotions,
    string? ManualDiscountReason);

/// <summary>A version in the quote history of a file.</summary>
public sealed record QuoteVersionSummary(
    Guid QuoteId,
    string Code,
    int Version,
    QuoteOrigin Origin,
    Money TotalInclVat,
    DateTimeOffset CreatedAtUtc,
    string? CreatedBy,
    string? ManualDiscountReason,
    bool IsCurrent);

/// <summary>Filters of the list of rental files; the period filters the pick-up date (Tunis days, inclusive).</summary>
public sealed record BookingListFilter(
    Guid? AgencyId = null,
    IReadOnlyList<BookingStatus>? Statuses = null,
    SalesChannel? Channel = null,
    Guid? CategoryId = null,
    DateOnly? From = null,
    DateOnly? To = null,
    string? Search = null);

/// <summary>A row of the list of rental files.</summary>
public sealed record BookingListItem(
    Guid Id,
    string Number,
    BookingStatus Status,
    SalesChannel Channel,
    DateTimeOffset PickupAt,
    DateTimeOffset ReturnAt,
    Guid CategoryId,
    string CategoryCode,
    Guid? VehicleId,
    string? PlateNumber,
    Guid? MainDriverId,
    string CustomerName,
    Money TotalInclVat,
    DateTimeOffset? Deadline,
    bool IsLate);

/// <summary>A driver of a file: identity summary, eligibility for the rental and state of the documents.</summary>
public sealed record DriverView(
    Guid PersonId,
    BookingPartyRoleInfo Role,
    string FullName,
    string? Phone,
    string? Email,
    bool IsAnonymized,
    DriverEligibilityInfo? Eligibility,
    DocumentsStateInfo Documents);

/// <summary>A row of the timeline of a file.</summary>
public sealed record TimelineEntryView(
    int Sequence,
    DateTimeOffset OccurredAtUtc,
    BookingEventType Type,
    BookingStatus? StatusBefore,
    BookingStatus StatusAfter,
    Guid? ActorUserId,
    string ActorName,
    SalesChannel? Channel,
    string? Reason,
    IReadOnlyDictionary<string, string> Facts);

/// <summary>The compliance waiver of the vehicle of a file.</summary>
public sealed record ComplianceWaiverView(Guid VehicleId, string ByName, DateTimeOffset AtUtc, string Reason);

/// <summary>Where a request came from.</summary>
public sealed record AttributionView(string? Source, string? UtmSource, string? UtmMedium, string? UtmCampaign, string? UtmTerm, string? UtmContent);

/// <summary>What the current user may do on a file in its status (transition table ∩ permissions).</summary>
public sealed record BookingFileActions(
    bool CanValidate,
    bool CanRequestInformation,
    bool CanProvideInformation,
    bool CanRefuse,
    bool CanConfirmOption,
    bool CanMarkPaid,
    bool CanCancel,
    bool CanMarkNoShow,
    DateTimeOffset? NoShowAllowedFrom,
    bool CanAmend,
    bool CanAssign,
    bool CanUnassign,
    bool CanDiscount,
    bool CanWaiveCompliance,
    bool CanWaiveCancellationFee,
    bool CanStartRental,
    bool CanCompleteReturn,
    bool CanClose)
{
    public bool Any => CanValidate || CanRequestInformation || CanProvideInformation || CanRefuse || CanConfirmOption || CanMarkPaid
                       || CanCancel || CanMarkNoShow || CanAmend || CanAssign || CanUnassign || CanStartRental || CanCompleteReturn || CanClose;
}

/// <summary>The detail page of a rental file.</summary>
public sealed record BookingFile(
    Guid Id,
    string Number,
    Guid AgencyId,
    BookingStatus Status,
    string? StatusReason,
    SalesChannel Channel,
    string? ChannelReference,
    AttributionView? Attribution,
    VehicleCategoryInfo? Category,
    Guid CategoryId,
    LocationInfo? PickupLocation,
    LocationInfo? ReturnLocation,
    DateTimeOffset PickupAt,
    DateTimeOffset ReturnAt,
    DateTimeOffset? ActualPickupAt,
    DateTimeOffset? ActualReturnAt,
    VehicleInfo? AssignedVehicle,
    Guid? GuaranteedVehicleId,
    ComplianceWaiverView? ComplianceWaiver,
    DateTimeOffset? HoldExpiresAt,
    DateTimeOffset? OptionExpiresAt,
    DateTimeOffset? PaymentDueAt,
    DateTimeOffset? EscalatedAtUtc,
    IReadOnlyList<DriverView> Drivers,
    CustomerInfo? Payer,
    IReadOnlyList<ExtraChoiceView> Extras,
    string? PromoCode,
    string? Notes,
    Money TotalInclVat,
    Money Deposit,
    Money? CancellationFee,
    int CurrentQuoteVersion,
    QuoteView? CurrentQuote,
    IReadOnlyList<QuoteVersionSummary> QuoteVersions,
    IReadOnlyList<TimelineEntryView> Timeline,
    BookingFileActions Actions,
    CancellationPreview? Cancellation,
    DateTimeOffset CreatedAtUtc,
    bool IsLate);

/// <summary>An extra chosen on a file, with its name when the rate plan still offers it.</summary>
public sealed record ExtraChoiceView(string Code, int Quantity, string? Name, string? NameEn);

/// <summary>State of an item of the validation checklist of a web request.</summary>
public enum ChecklistState
{
    /// <summary>Checked by the system: nothing to report.</summary>
    Ok = 1,

    /// <summary>The system found an issue: request information or refuse.</summary>
    Attention = 2,

    /// <summary>The system cannot check it: the agent confirms it on the documents.</summary>
    Manual = 3,

    /// <summary>Not available yet (payment pre-authorization, phase 2).</summary>
    Pending = 4,
}

/// <summary>Items of the validation checklist (SPEC §6), in display order.</summary>
public enum ChecklistItemKind
{
    /// <summary>Identity document and driving licence of each driver present, with their images, verified.</summary>
    Documents = 1,

    /// <summary>Same names on the documents, the licence and the request (checked by the agent).</summary>
    Names = 2,

    /// <summary>Age and licence seniority of each driver.</summary>
    AgeAndLicense = 3,

    /// <summary>Documents valid until the return, international permit when needed.</summary>
    Validity = 4,

    /// <summary>Internal customer check (never more specific).</summary>
    CustomerCheck = 5,

    /// <summary>The category is still available for the period.</summary>
    Availability = 6,

    /// <summary>Payment pre-authorization (Clic to Pay, phase 2).</summary>
    Payment = 7,
}

/// <summary>
/// An item of the checklist and the details shown with it (eligibility issues, driver names); the payment item carries
/// the state of the online payment of the request.
/// </summary>
public sealed record ChecklistItem(ChecklistItemKind Kind, ChecklistState State, IReadOnlyList<ChecklistDetail> Details, ChecklistPayment? Payment = null);

/// <summary>
/// A detail of a checklist item: the driver it concerns and an eligibility issue, when any;
/// <see cref="SubmissionPending"/> when documents sent online for the request wait for a review (driver's page).
/// </summary>
public sealed record ChecklistDetail(string DriverName, EligibilityIssueInfo? Issue, bool SubmissionPending = false, Guid? PersonId = null);

/// <summary>
/// The payment item of the checklist: where the online payment of the request stands (kept from the Payments module),
/// the amount held or taken, a payment made after its expiry, and the payment mode of the validation.
/// </summary>
public sealed record ChecklistPayment(RequestPaymentState State, Money? Amount, bool PaidLate, BookingValidationPaymentMode Mode);

/// <summary>The pre-filled validation checklist of a web request.</summary>
public sealed record RequestChecklist(Guid BookingId, IReadOnlyList<ChecklistItem> Items, IReadOnlyList<DriverView> Drivers)
{
    public bool AllOk => Items.All(item => item.State is ChecklistState.Ok or ChecklistState.Manual or ChecklistState.Pending);
}

/// <summary>Whether a vehicle of the category can carry the file (checked on fresh data, without lock).</summary>
public enum VehicleCandidateState
{
    Free = 1,
    Overlap = 2,
    NotCompliant = 3,
    Unavailable = 4,
}

/// <summary>A vehicle offered by the assignment dialog, with its pre-check.</summary>
public sealed record VehicleCandidate(
    Guid VehicleId,
    string PlateNumber,
    string Make,
    string Model,
    VehicleStatusInfo Status,
    DateTimeOffset? CompliantUntil,
    VehicleCandidateState State,
    bool IsCurrent);

/// <summary>The bookings part of the daily dashboard.</summary>
public sealed record BookingsDashboard(
    DayAgendaInfo Today,
    IReadOnlyList<PendingRequestInfo>? Requests,
    IReadOnlyList<LateReturnInfo> LateReturns,
    IReadOnlyList<BookingListItem> UnassignedSoon,
    IReadOnlyList<CapacityShortfallInfo> Shortfalls);

/// <summary>What the quick creation screen needs: places, categories, extras and the default option duration.</summary>
public sealed record QuickCreateContext(
    AgencyInfo Agency,
    IReadOnlyList<LocationInfo> Locations,
    IReadOnlyList<VehicleCategoryInfo> Categories,
    IReadOnlyList<ExtraOffer> Extras,
    TimeSpan OptionDefaultDuration);

/// <summary>The rental the quick creation screen prices for every category (live table).</summary>
public sealed record QuickQuoteRequest(
    Guid AgencyId,
    SalesChannel Channel,
    Guid PickupLocationId,
    Guid ReturnLocationId,
    DateTimeOffset PickupAt,
    DateTimeOffset ReturnAt,
    IReadOnlyList<SelectedExtra>? Extras = null,
    string? PromoCode = null,
    IReadOnlyList<Guid>? DriverIds = null);

/// <summary>A category of the live table: vehicles left for the period and its total price (or why it has none).</summary>
public sealed record CategoryQuoteRow(
    VehicleCategoryInfo Category,
    bool IsAvailable,
    int Remaining,
    Money? TotalInclVat,
    Money? Deposit,
    int? Days,
    PromoCodeOutcome PromoCodeOutcome,
    Error? PriceError);
