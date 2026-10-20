using CarRental.Application.Fleet.Contracts;
using CarRental.Domain.Common;

namespace CarRental.Testing;

/// <summary>
/// In-memory Fleet contract used by the test hosts until the Fleet module implements <see cref="IFleetQueries"/>
/// (<see cref="CarRentalAppFactory"/> registers it only when no implementation is registered). Every agency sees the five
/// demo categories (fixed ids) and two ECMR vehicles, one of them offered as a guaranteed model (15.000 TND per day).
/// </summary>
public sealed class StubFleetQueries : IFleetQueries
{
    public static readonly Guid Mcmr = Guid.Parse("0199c000-0000-7000-8000-00000000c001");
    public static readonly Guid Ecmr = Guid.Parse("0199c000-0000-7000-8000-00000000c002");
    public static readonly Guid Edar = Guid.Parse("0199c000-0000-7000-8000-00000000c003");
    public static readonly Guid Cdmr = Guid.Parse("0199c000-0000-7000-8000-00000000c004");
    public static readonly Guid Ifmr = Guid.Parse("0199c000-0000-7000-8000-00000000c005");

    /// <summary>ECMR vehicle with a guaranteed-model supplement of 15.000 TND per day.</summary>
    public static readonly Guid GuaranteedEcmrVehicle = Guid.Parse("0199c000-0000-7000-8000-00000000d001");

    /// <summary>ECMR vehicle without guaranteed-model supplement.</summary>
    public static readonly Guid PlainEcmrVehicle = Guid.Parse("0199c000-0000-7000-8000-00000000d002");

    private static readonly (Guid Id, string Code, string Name, string Model, TransmissionInfo Transmission, int Seats, decimal Deposit, decimal ReducedDeposit)[] Categories =
    [
        (Mcmr, "MCMR", "Mini", "Kia Picanto", TransmissionInfo.Manual, 4, 500m, 250m),
        (Ecmr, "ECMR", "Économique", "Peugeot 208", TransmissionInfo.Manual, 5, 600m, 300m),
        (Edar, "EDAR", "Économique automatique", "Hyundai i20", TransmissionInfo.Automatic, 5, 700m, 350m),
        (Cdmr, "CDMR", "Compacte", "Volkswagen Golf", TransmissionInfo.Manual, 5, 800m, 400m),
        (Ifmr, "IFMR", "SUV intermédiaire", "Hyundai Tucson", TransmissionInfo.Manual, 5, 1000m, 500m),
    ];

    public Task<IReadOnlyList<VehicleCategoryInfo>> GetCategoriesAsync(
        Guid? agencyId = null,
        bool includeInactive = false,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<VehicleCategoryInfo>>([.. Categories.Select((_, index) => Category(index, agencyId ?? Guid.Empty))]);

    public Task<VehicleCategoryInfo?> GetCategoryAsync(Guid categoryId, CancellationToken cancellationToken = default)
    {
        var index = Array.FindIndex(Categories, c => c.Id == categoryId);
        return Task.FromResult(index < 0 ? null : Category(index, Guid.Empty));
    }

    public Task<VehicleCategoryInfo?> FindCategoryByCodeAsync(Guid agencyId, string acrissCode, CancellationToken cancellationToken = default)
    {
        var index = Array.FindIndex(Categories, c => string.Equals(c.Code, acrissCode?.Trim(), StringComparison.OrdinalIgnoreCase));
        return Task.FromResult(index < 0 ? null : Category(index, agencyId));
    }

    public Task<VehicleInfo?> GetVehicleAsync(Guid vehicleId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Vehicles().FirstOrDefault(v => v.Id == vehicleId));

    public Task<IReadOnlyList<VehicleInfo>> GetVehiclesAsync(Guid categoryId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<VehicleInfo>>([.. Vehicles().Where(v => v.CategoryId == categoryId)]);

    public Task<IReadOnlyList<VehicleAvailabilityInfo>> GetAvailabilityAsync(Guid categoryId, Period range, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(range);
        return Task.FromResult<IReadOnlyList<VehicleAvailabilityInfo>>(
            [.. Vehicles().Where(v => v.CategoryId == categoryId).Select(v => new VehicleAvailabilityInfo(v.Id, v.PlateNumber, [range]))]);
    }

    private static VehicleCategoryInfo Category(int index, Guid agencyId)
    {
        var (id, code, name, model, transmission, seats, deposit, reducedDeposit) = Categories[index];
        return new VehicleCategoryInfo(
            id,
            agencyId,
            code,
            name,
            model,
            seats,
            Doors: 5,
            LargeBags: 1,
            SmallBags: 2,
            transmission,
            FuelTypeInfo.Petrol,
            AirConditioning: true,
            Money.Tnd(deposit),
            Money.Tnd(reducedDeposit),
            PhotoUrl: null,
            DisplayOrder: index,
            IsActive: true);
    }

    private static IEnumerable<VehicleInfo> Vehicles()
    {
        yield return Vehicle(GuaranteedEcmrVehicle, "100 TU 1001", Money.Tnd(15m));
        yield return Vehicle(PlainEcmrVehicle, "100 TU 1002", null);
    }

    private static VehicleInfo Vehicle(Guid id, string plate, Money? supplement) => new(
        id,
        Guid.Empty,
        Ecmr,
        plate,
        "Peugeot",
        "208",
        2025,
        TransmissionInfo.Manual,
        FuelTypeInfo.Petrol,
        5,
        12_000,
        VehicleStatusInfo.Active,
        supplement,
        CompliantUntil: DateTimeOffset.MaxValue);
}
