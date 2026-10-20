using CarRental.Application.Fleet.Contracts;
using CarRental.Domain.Common;

namespace CarRental.Application.Bookings.Contracts;

/// <summary>Status of a rental file, as exposed to other modules (same values as the domain enum).</summary>
public enum BookingStatusInfo
{
    Draft = 1,
    PendingValidation = 2,
    InformationRequested = 3,
    OptionHeld = 4,
    AwaitingPayment = 5,
    Confirmed = 6,
    InProgress = 7,
    Returned = 8,
    Closed = 9,
    Refused = 10,
    Expired = 11,
    Cancelled = 12,
    NoShow = 13,
}

/// <summary>Role of a person in a rental file.</summary>
public enum BookingPartyRoleInfo
{
    MainDriver = 1,
    SecondaryDriver = 2,
    Payer = 3,
}

/// <summary>
/// A rental file: identity, status, channel, category, places and instants (planned and actual), vehicle, parties
/// (Customers ids), current quote and its totals (TTC; the deposit is outside the total), deadlines of the status.
/// </summary>
public sealed record BookingSummaryInfo(
    Guid Id,
    string Number,
    Guid AgencyId,
    BookingStatusInfo Status,
    SalesChannel Channel,
    string? ChannelReference,
    Guid CategoryId,
    Guid PickupLocationId,
    Guid ReturnLocationId,
    DateTimeOffset PickupAt,
    DateTimeOffset ReturnAt,
    DateTimeOffset? ActualPickupAt,
    DateTimeOffset? ActualReturnAt,
    Guid? AssignedVehicleId,
    Guid? GuaranteedVehicleId,
    Guid? MainDriverId,
    Guid? SecondaryDriverId,
    Guid? PayerCustomerId,
    Guid CurrentQuoteId,
    int CurrentQuoteVersion,
    Money TotalInclVat,
    Money Deposit,
    Money? CancellationFee,
    DateTimeOffset? HoldExpiresAt,
    DateTimeOffset? OptionExpiresAt,
    DateTimeOffset? PaymentDueAt,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset StatusChangedAtUtc);

/// <summary>A rental file of a person and the roles the person holds in it (driver, second driver, payer).</summary>
public sealed record PersonBookingInfo(BookingSummaryInfo Booking, string CategoryCode, IReadOnlyList<BookingPartyRoleInfo> Roles);

/// <summary>State of the documents of the drivers of a departure (eligibility and verification).</summary>
public enum DocumentsStateInfo
{
    /// <summary>Not known (no driver yet).</summary>
    Unknown = 0,

    /// <summary>A mandatory document is missing or not valid until the return.</summary>
    Missing = 1,

    /// <summary>Documents present but not all verified by an agent.</summary>
    ToVerify = 2,

    /// <summary>Every document present, valid until the return and verified.</summary>
    Complete = 3,
}

/// <summary>
/// Payment state of a departure. Phase 1E only knows the fallback deadline (<see cref="AwaitingPayment"/>); the
/// Payments module (1F-a) fills the balance.
/// </summary>
public enum PaymentStateInfo
{
    Unknown = 0,
    AwaitingPayment = 1,
}

/// <summary>A departure or a return of the day (tableau du jour).</summary>
public sealed record AgendaItemInfo(
    Guid BookingId,
    string Number,
    BookingStatusInfo Status,
    DateTimeOffset At,
    Guid CategoryId,
    string CategoryCode,
    Guid? VehicleId,
    string? PlateNumber,
    Guid? MainDriverId,
    string CustomerName,
    string? Phone,
    DocumentsStateInfo Documents,
    PaymentStateInfo Payment,
    bool IsDone,
    bool IsLate);

/// <summary>Departures and returns of an agency on a Tunis day, ordered by time.</summary>
public sealed record DayAgendaInfo(Guid AgencyId, DateOnly Date, IReadOnlyList<AgendaItemInfo> Departures, IReadOnlyList<AgendaItemInfo> Returns);

/// <summary>A web request waiting for an agent, with the end of its hold and whether it was escalated.</summary>
public sealed record PendingRequestInfo(
    Guid BookingId,
    string Number,
    BookingStatusInfo Status,
    SalesChannel Channel,
    DateTimeOffset SubmittedAt,
    DateTimeOffset HoldExpiresAt,
    bool IsEscalated,
    Guid CategoryId,
    string CategoryCode,
    DateTimeOffset PickupAt,
    DateTimeOffset ReturnAt,
    Money TotalInclVat,
    Guid? MainDriverId,
    string CustomerName)
{
    /// <summary>Time left before the request expires (zero once passed).</summary>
    public TimeSpan TimeLeft(DateTimeOffset now) => HoldExpiresAt > now ? HoldExpiresAt - now : TimeSpan.Zero;
}

/// <summary>A rental in progress whose planned return has passed.</summary>
public sealed record LateReturnInfo(
    Guid BookingId,
    string Number,
    DateTimeOffset ReturnAt,
    Guid CategoryId,
    Guid? VehicleId,
    string? PlateNumber,
    Guid? MainDriverId,
    string CustomerName,
    string? Phone);

/// <summary>Why a vehicle cannot be rented during a period of the planning.</summary>
public enum UnavailabilityReasonInfo
{
    /// <summary>A dated maintenance block (or not in service yet).</summary>
    Maintenance = 1,

    /// <summary>Status <c>Maintenance</c> or <c>OutOfService</c> without end.</summary>
    OutOfService = 2,

    /// <summary>A mandatory document (insurance, road tax, technical inspection) has expired or is missing.</summary>
    NonCompliant = 3,
}

/// <summary>A period during which a vehicle cannot be rented, and why.</summary>
public sealed record PlanningUnavailabilityInfo(Period Period, UnavailabilityReasonInfo Reason);

/// <summary>A rental file in the planning: planned and actual instants and its occupancy (preparation buffer included).</summary>
public sealed record PlanningBookingInfo(
    Guid BookingId,
    string Number,
    BookingStatusInfo Status,
    Guid CategoryId,
    DateTimeOffset PickupAt,
    DateTimeOffset ReturnAt,
    DateTimeOffset? ActualPickupAt,
    DateTimeOffset? ActualReturnAt,
    Period Occupancy,
    Guid? AssignedVehicleId,
    bool IsGuaranteedModel,
    bool IsComplianceWaived,
    string CustomerName,
    bool IsLate);

/// <summary>A vehicle row of the planning: rentable windows, unavailability with its reason and its rental files.</summary>
public sealed record PlanningVehicleInfo(
    Guid VehicleId,
    string PlateNumber,
    string Make,
    string Model,
    VehicleStatusInfo Status,
    DateTimeOffset? CompliantUntil,
    IReadOnlyList<Period> RentableWindows,
    IReadOnlyList<PlanningUnavailabilityInfo> Unavailability,
    IReadOnlyList<PlanningBookingInfo> Bookings);

/// <summary>A period during which a category has fewer vehicles than rental files: <c>Missing</c> vehicles at least.</summary>
public sealed record CapacityShortfallInfo(Guid CategoryId, string CategoryCode, Period Period, int Missing);

/// <summary>A category of the planning: its vehicles, the files not assigned yet, its capacity profile and shortfalls.</summary>
public sealed record PlanningCategoryInfo(
    VehicleCategoryInfo Category,
    IReadOnlyList<PlanningVehicleInfo> Vehicles,
    IReadOnlyList<PlanningBookingInfo> Unassigned,
    IReadOnlyList<CapacitySegmentInfo> Capacity,
    IReadOnlyList<CapacityShortfallInfo> Shortfalls);

/// <summary>The planning of an agency over <c>Range</c> (vehicles × days).</summary>
public sealed record PlanningInfo(Guid AgencyId, Period Range, TimeSpan PreparationBuffer, IReadOnlyList<PlanningCategoryInfo> Categories);

/// <summary>
/// Outbox message published with every status change of a rental file (creation included), in the same transaction:
/// the extension point of the payment capture (phase 2, Clic to Pay), the e-mails and the channels. Handlers register
/// with <c>services.AddOutboxHandler&lt;BookingStatusChanged, THandler&gt;()</c> and must be idempotent.
/// </summary>
/// <param name="BookingId">The file.</param>
/// <param name="AgencyId">Its agency.</param>
/// <param name="Number">Its readable number.</param>
/// <param name="Channel">Its sales channel.</param>
/// <param name="PreviousStatus">Status before the change, <see langword="null"/> at the creation.</param>
/// <param name="Status">Status after the change.</param>
/// <param name="TotalInclVat">Total TTC of the current quote.</param>
/// <param name="PaymentDueAt">Payment deadline in the fallback payment mode (<c>AwaitingPayment</c>).</param>
/// <param name="OccurredAtUtc">When the change happened.</param>
public sealed record BookingStatusChanged(
    Guid BookingId,
    Guid AgencyId,
    string Number,
    SalesChannel Channel,
    BookingStatusInfo? PreviousStatus,
    BookingStatusInfo Status,
    Money TotalInclVat,
    DateTimeOffset? PaymentDueAt,
    DateTimeOffset OccurredAtUtc);
