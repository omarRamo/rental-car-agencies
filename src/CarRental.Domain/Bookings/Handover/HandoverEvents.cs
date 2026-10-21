using CarRental.Domain.Common;

namespace CarRental.Domain.Bookings.Handover;

/// <summary>An inspection (departure or return) was started as a draft.</summary>
public sealed record InspectionStarted(Guid InspectionId, Guid BookingId, InspectionKind Kind, Guid? VehicleId) : IDomainEvent;

/// <summary>The vehicle of a departure inspection was confirmed (the readings and photos are about this vehicle).</summary>
public sealed record InspectionVehicleConfirmed(Guid InspectionId, Guid BookingId, Guid VehicleId, int PreExistingDamages) : IDomainEvent;

/// <summary>An inspection was completed with the departure or the return of its rental file.</summary>
public sealed record InspectionCompleted(
    Guid InspectionId,
    Guid BookingId,
    InspectionKind Kind,
    Guid VehicleId,
    int Mileage,
    int FuelEighths,
    int NewDamages,
    DateTimeOffset HandedOverAtUtc) : IDomainEvent;

/// <summary>A manager let the vehicle leave without the balance paid or without a deposit.</summary>
public sealed record DeparturePaymentDeferred(Guid InspectionId, Guid BookingId, Money BalanceDeferred, bool DepositWaived) : IDomainEvent;

/// <summary>The charges of a return were recorded (with the return) or revised (before the closing).</summary>
public sealed record ReturnChargesRecorded(Guid InspectionId, Guid BookingId, Money TotalInclVat, int Lines, bool IsRevision) : IDomainEvent;

/// <summary>A rental contract (PDF) was generated and stored.</summary>
public sealed record RentalContractGenerated(Guid ContractId, Guid BookingId, string Number, int Version) : IDomainEvent;

/// <summary>The rental contract was queued for e-mail to the main driver (outbox).</summary>
public sealed record RentalContractSent(Guid ContractId, Guid BookingId) : IDomainEvent;
