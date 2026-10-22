using CarRental.Domain.Common;

namespace CarRental.Application.Fleet.Contracts;

/// <summary>
/// Public read API of the Fleet module for other modules (ids and contract DTOs only).
/// Semantics of availability and compliance: docs/conception/phase-1-exploitation.md §3 and §5.
/// </summary>
public interface IFleetQueries
{
    /// <summary>
    /// Vehicle categories of an agency (when <paramref name="agencyId"/> is null, of every agency the operation sees: its
    /// own for an operation of one agency, ADR-0067), ordered by display order then ACRISS code.
    /// </summary>
    Task<IReadOnlyList<VehicleCategoryInfo>> GetCategoriesAsync(
        Guid? agencyId = null,
        bool includeInactive = false,
        CancellationToken cancellationToken = default);

    Task<VehicleCategoryInfo?> GetCategoryAsync(Guid categoryId, CancellationToken cancellationToken = default);

    /// <summary>Finds an active category of an agency by its ACRISS code (deep links carry the code, e.g. <c>cat=ECMR</c>).</summary>
    Task<VehicleCategoryInfo?> FindCategoryByCodeAsync(
        Guid agencyId,
        string acrissCode,
        CancellationToken cancellationToken = default);

    Task<VehicleInfo?> GetVehicleAsync(Guid vehicleId, CancellationToken cancellationToken = default);

    /// <summary>Vehicles of a category that are not sold (any other status), ordered by plate number.</summary>
    Task<IReadOnlyList<VehicleInfo>> GetVehiclesAsync(Guid categoryId, CancellationToken cancellationToken = default);

    /// <summary>
    /// For each non-sold vehicle of the category, the sub-periods of <paramref name="range"/> during which it can be
    /// rented: in service, before its sale, covered by all mandatory documents, not blocked by maintenance and not in
    /// the <c>Maintenance</c> / <c>OutOfService</c> status (which blocks from now on, without end, unless a dated block
    /// is planned). The capacity of the category at instant t is the number of vehicles having a window that contains t.
    /// </summary>
    /// <remarks>Windows are half-open, ordered, non-overlapping and clipped to <paramref name="range"/>; a vehicle
    /// without any window in the range is still listed (empty list) so that callers see the whole category.</remarks>
    Task<IReadOnlyList<VehicleAvailabilityInfo>> GetAvailabilityAsync(
        Guid categoryId,
        Period range,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deadlines of a vehicle: its current document of each type (ordered by type), so that a booking can explain which
    /// document stops an assignment (invariant 3). A missing mandatory type is absent; empty for an unknown vehicle.
    /// </summary>
    Task<IReadOnlyList<VehicleDeadlineInfo>> GetDeadlinesAsync(Guid vehicleId, CancellationToken cancellationToken = default);
}

/// <summary>
/// The current document of a type. <c>ValidUntil</c> is the end (exclusive) of its validity: the end of the Tunis day of
/// <c>ExpiresOn</c>; both are <see langword="null"/> for a document without expiry (registration card).
/// </summary>
public sealed record VehicleDeadlineInfo(
    VehicleDocumentTypeInfo Type,
    string Number,
    DateOnly? ExpiresOn,
    DateTimeOffset? ValidUntil,
    bool IsMandatory);

/// <summary>Type of a vehicle document, as exposed to other modules (same values as the domain enum).</summary>
public enum VehicleDocumentTypeInfo
{
    Insurance = 1,
    RoadTax = 2,
    TechnicalInspection = 3,
    RegistrationCard = 4,
}

/// <summary>
/// A vehicle category sold "or similar" (ACRISS code). <c>ExampleModel</c> is shown to customers (e.g. "Peugeot 208
/// or similar"); <c>Deposit</c> is the deposit (caution) of the category and <c>ReducedDeposit</c> the deposit when the
/// customer buys the franchise waiver (rachat de franchise); <c>PhotoUrl</c> is the app-relative URL of the public
/// category photo, or <see langword="null"/>.
/// </summary>
public sealed record VehicleCategoryInfo(
    Guid Id,
    Guid AgencyId,
    string AcrissCode,
    string Name,
    string ExampleModel,
    int Seats,
    int Doors,
    int LargeBags,
    int SmallBags,
    TransmissionInfo Transmission,
    FuelTypeInfo Fuel,
    bool AirConditioning,
    Money Deposit,
    Money ReducedDeposit,
    string? PhotoUrl,
    int DisplayOrder,
    bool IsActive);

/// <summary>
/// A vehicle (registration plate) attached to a category. <c>GuaranteedModelDailySupplement</c> is the daily
/// supplement (VAT included) when a customer books this exact vehicle ("modèle garanti"), <see langword="null"/> when
/// not offered. <c>CompliantUntil</c> is the end (exclusive) of the period covered by all mandatory documents
/// (insurance, road tax, technical inspection), i.e. the end of the Tunis day of the earliest expiry;
/// <see langword="null"/> when a mandatory document is missing — the vehicle is then not compliant at all.
/// </summary>
public sealed record VehicleInfo(
    Guid Id,
    Guid AgencyId,
    Guid CategoryId,
    string PlateNumber,
    string Make,
    string Model,
    int Year,
    TransmissionInfo Transmission,
    FuelTypeInfo Fuel,
    int Seats,
    int Mileage,
    VehicleStatusInfo Status,
    Money? GuaranteedModelDailySupplement,
    DateTimeOffset? CompliantUntil)
{
    /// <summary>Whether all mandatory documents stay valid until the end of <paramref name="period"/> (invariant 3).</summary>
    public bool IsCompliantFor(Period period)
    {
        ArgumentNullException.ThrowIfNull(period);
        return CompliantUntil is { } until && period.End <= until;
    }
}

/// <summary>Rentable windows of one vehicle within a requested range.</summary>
public sealed record VehicleAvailabilityInfo(Guid VehicleId, string PlateNumber, IReadOnlyList<Period> Windows);

/// <summary>Gearbox, as exposed to other modules (same values as the domain enum).</summary>
public enum TransmissionInfo
{
    Manual = 1,
    Automatic = 2,
}

/// <summary>Fuel or energy, as exposed to other modules (same values as the domain enum).</summary>
public enum FuelTypeInfo
{
    Petrol = 1,
    Diesel = 2,
    Hybrid = 3,
    Electric = 4,
    Lpg = 5,
}

/// <summary>Operational status of a vehicle, as exposed to other modules (same values as the domain enum).</summary>
public enum VehicleStatusInfo
{
    Active = 1,
    Maintenance = 2,
    OutOfService = 3,
    Sold = 4,
}
