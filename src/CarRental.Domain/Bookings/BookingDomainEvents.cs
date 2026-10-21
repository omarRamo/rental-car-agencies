using CarRental.Domain.Common;

namespace CarRental.Domain.Bookings;

// Domain events of the rental file, written to the outbox with the change (ADR-0013). One per command; every event
// carries the file, its agency and its category so that handlers never need to reload the aggregate for routing.

/// <summary>A rental file was created (<c>Draft</c>, <c>OptionHeld</c> or <c>Confirmed</c>).</summary>
public sealed record BookingCreated(
    Guid BookingId,
    Guid AgencyId,
    string Number,
    SalesChannel Channel,
    BookingStatus Status,
    Guid CategoryId,
    DateTimeOffset PickupAt,
    DateTimeOffset ReturnAt,
    Money TotalInclVat) : IDomainEvent;

/// <summary>A web request was submitted and holds capacity until <c>HoldExpiresAt</c>.</summary>
public sealed record BookingSubmitted(Guid BookingId, Guid AgencyId, Guid CategoryId, string Number, DateTimeOffset HoldExpiresAt) : IDomainEvent;

public sealed record BookingInformationRequested(
    Guid BookingId,
    Guid AgencyId,
    Guid CategoryId,
    InformationRequestReason Reason,
    string Message,
    DateTimeOffset HoldExpiresAt) : IDomainEvent;

public sealed record BookingInformationProvided(Guid BookingId, Guid AgencyId, Guid CategoryId) : IDomainEvent;

/// <summary>A request was validated: <c>Confirmed</c>, or <c>AwaitingPayment</c> until <c>PaymentDueAt</c> in the fallback payment mode.</summary>
public sealed record BookingValidated(Guid BookingId, Guid AgencyId, Guid CategoryId, BookingStatus Status, DateTimeOffset? PaymentDueAt) : IDomainEvent;

public sealed record BookingRefused(Guid BookingId, Guid AgencyId, Guid CategoryId, BookingStatus PreviousStatus, string Reason) : IDomainEvent;

/// <summary>A hold, an option or a payment deadline passed: the capacity is released.</summary>
public sealed record BookingExpired(Guid BookingId, Guid AgencyId, Guid CategoryId, BookingStatus PreviousStatus) : IDomainEvent;

public sealed record BookingOptionConfirmed(Guid BookingId, Guid AgencyId, Guid CategoryId) : IDomainEvent;

public sealed record BookingPaid(Guid BookingId, Guid AgencyId, Guid CategoryId, string? PaymentReference) : IDomainEvent;

/// <summary>A file was cancelled; <c>CancellationFee</c> follows the cancellation policy (zero when free or waived).</summary>
public sealed record BookingCancelled(
    Guid BookingId,
    Guid AgencyId,
    Guid CategoryId,
    BookingStatus PreviousStatus,
    string Reason,
    Money CancellationFee,
    bool FeeWaived) : IDomainEvent;

public sealed record BookingNoShowRecorded(Guid BookingId, Guid AgencyId, Guid CategoryId, string Reason) : IDomainEvent;

/// <summary>Departure: the vehicle left at <c>ActualPickupAt</c>.</summary>
public sealed record BookingRentalStarted(Guid BookingId, Guid AgencyId, Guid CategoryId, Guid VehicleId, DateTimeOffset ActualPickupAt) : IDomainEvent;

/// <summary>Return: the vehicle came back at <c>ActualReturnAt</c>.</summary>
public sealed record BookingReturned(Guid BookingId, Guid AgencyId, Guid CategoryId, Guid? VehicleId, DateTimeOffset ActualReturnAt) : IDomainEvent;

public sealed record BookingClosed(Guid BookingId, Guid AgencyId, Guid CategoryId, DateTimeOffset ClosedAtUtc) : IDomainEvent;

/// <summary>
/// The file was amended (dates, locations, category, extras, drivers, payer) or extended; <c>QuoteVersion</c> is the
/// current quote version afterwards; <c>PreviousCategoryId</c> is set when the category changed.
/// </summary>
public sealed record BookingAmended(
    Guid BookingId,
    Guid AgencyId,
    Guid CategoryId,
    Guid? PreviousCategoryId,
    int QuoteVersion,
    bool IsExtension) : IDomainEvent;

/// <summary>A vehicle was assigned (or swapped during the rental); <c>ComplianceWaived</c> when a manager waived its documents.</summary>
public sealed record BookingVehicleAssigned(
    Guid BookingId,
    Guid AgencyId,
    Guid CategoryId,
    Guid VehicleId,
    Guid? PreviousVehicleId,
    bool ComplianceWaived) : IDomainEvent;

public sealed record BookingVehicleUnassigned(Guid BookingId, Guid AgencyId, Guid CategoryId, Guid PreviousVehicleId) : IDomainEvent;

/// <summary>A web request stayed pending beyond the escalation delay: the managers are told once.</summary>
public sealed record BookingEscalated(Guid BookingId, Guid AgencyId, Guid CategoryId, string Number) : IDomainEvent;
