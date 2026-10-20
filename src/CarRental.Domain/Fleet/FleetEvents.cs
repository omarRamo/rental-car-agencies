using CarRental.Domain.Common;

namespace CarRental.Domain.Fleet;

// Categories
public sealed record VehicleCategoryCreated(Guid CategoryId, Guid AgencyId, AcrissCode Code) : IDomainEvent;

public sealed record VehicleCategoryUpdated(Guid CategoryId, AcrissCode Code) : IDomainEvent;

public sealed record VehicleCategoryActivationChanged(Guid CategoryId, bool IsActive) : IDomainEvent;

// Vehicles

/// <summary>
/// Raised once per operation that changes the rentable windows of a vehicle (registration, category, status, sale,
/// mandatory documents, maintenance blocks, date put in service): the single event the Bookings module needs to
/// re-check the assignments of the vehicle. <see cref="PreviousCategoryId"/> is set when the vehicle changed category.
/// </summary>
public sealed record VehicleAvailabilityChanged(Guid VehicleId, Guid AgencyId, Guid CategoryId, Guid? PreviousCategoryId) : IDomainEvent;

public sealed record VehicleRegistered(Guid VehicleId, Guid AgencyId, Guid CategoryId, string PlateNumber) : IDomainEvent;

public sealed record VehicleDetailsUpdated(Guid VehicleId, string PlateNumber) : IDomainEvent;

public sealed record VehicleCategoryChanged(Guid VehicleId, Guid PreviousCategoryId, Guid CategoryId) : IDomainEvent;

public sealed record VehicleStatusChanged(Guid VehicleId, Guid CategoryId, VehicleStatus PreviousStatus, VehicleStatus Status, string? Reason) : IDomainEvent;

public sealed record VehicleSold(Guid VehicleId, Guid CategoryId, DateTimeOffset SoldAtUtc, Money SalePrice) : IDomainEvent;

public sealed record VehicleMileageRecorded(Guid VehicleId, int PreviousMileage, int Mileage, bool IsCorrection) : IDomainEvent;

/// <summary>The "guaranteed model" daily supplement (VAT included) changed; <see langword="null"/> when no longer offered.</summary>
public sealed record GuaranteedModelSupplementChanged(Guid VehicleId, Guid CategoryId, Money? DailySupplement) : IDomainEvent;

public sealed record VehicleDocumentRecorded(
    Guid VehicleId,
    Guid CategoryId,
    Guid DocumentId,
    VehicleDocumentType DocumentType,
    DateOnly? ExpiresOn) : IDomainEvent;

public sealed record VehicleDocumentCorrected(
    Guid VehicleId,
    Guid CategoryId,
    Guid DocumentId,
    VehicleDocumentType DocumentType,
    DateOnly? ExpiresOn) : IDomainEvent;

// Maintenance
public sealed record MaintenanceScheduled(Guid VehicleId, Guid CategoryId, Guid MaintenanceId, MaintenanceType Type, Period Block) : IDomainEvent;

public sealed record MaintenanceRescheduled(Guid VehicleId, Guid CategoryId, Guid MaintenanceId, Period Block) : IDomainEvent;

public sealed record MaintenanceStarted(Guid VehicleId, Guid CategoryId, Guid MaintenanceId, Period Block) : IDomainEvent;

public sealed record MaintenanceCompleted(Guid VehicleId, Guid CategoryId, Guid MaintenanceId, DateTimeOffset CompletedAtUtc) : IDomainEvent;

public sealed record MaintenanceCancelled(Guid VehicleId, Guid CategoryId, Guid MaintenanceId) : IDomainEvent;
