using CarRental.Domain.Common;
using CarRental.Domain.Fleet;

namespace CarRental.Domain.Tests.Fleet;

/// <summary>Builders of valid fleet objects. Instants are given in Tunis local time (UTC+1, no daylight saving time).</summary>
internal static class FleetTestData
{
    public static readonly Guid AgencyId = Guid.CreateVersion7();
    public static readonly Guid CategoryId = Guid.CreateVersion7();

    /// <summary>2026-10-20 10:00 in Tunis.</summary>
    public static readonly DateTimeOffset Now = Tunis(2026, 10, 20, 10);

    public static DateTimeOffset Tunis(int year, int month, int day, int hour = 0, int minute = 0) =>
        TunisTime.FromLocal(new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Unspecified));

    public static ChangeStamp Stamp(DateTimeOffset? at = null, string by = "gerant@demo.local") => new(at ?? Now, by);

    public static Period P(DateTimeOffset start, DateTimeOffset end) => Period.Create(start, end).Value;

    public static VehicleDetails Details(string plate = "215 TU 4512", DateOnly? inServiceOn = null) => new(
        plate,
        "Peugeot",
        "208",
        2024,
        "Blanc",
        Transmission.Manual,
        FuelType.Petrol,
        Seats: 5,
        LargeBags: 1,
        SmallBags: 2,
        InServiceOn: inServiceOn ?? new DateOnly(2024, 3, 1),
        FirstRegistrationOn: new DateOnly(2024, 2, 15));

    public static Vehicle NewVehicle(int mileage = 20_000, DateOnly? inServiceOn = null, bool compliant = true)
    {
        var vehicle = Vehicle.Register(AgencyId, CategoryId, Details(inServiceOn: inServiceOn), mileage, Stamp(Now.AddYears(-1))).Value;
        if (compliant)
        {
            AddMandatoryDocuments(vehicle, new DateOnly(2027, 6, 30));
        }

        vehicle.DequeueDomainEvents();
        return vehicle;
    }

    public static void AddMandatoryDocuments(Vehicle vehicle, DateOnly expiresOn)
    {
        foreach (var type in VehicleCompliance.MandatoryTypes)
        {
            vehicle.RecordDocument(Document(type, expiresOn), Stamp()).IsSuccess.ShouldBeTrue();
        }
    }

    public static VehicleDocumentDetails Document(VehicleDocumentType type, DateOnly? expiresOn, string number = "N-001") =>
        new(type, number, IssuedOn: expiresOn?.AddYears(-1), ExpiresOn: expiresOn, Provider: "STAR Assurances", Cost: Money.Tnd(450m));

    public static MaintenanceDetails Maintenance(DateTimeOffset start, DateTimeOffset end, MaintenanceType type = MaintenanceType.OilChange) =>
        new(type, start, end, "Vidange 10 000 km", "Garage Central", Money.Tnd(180m));

    public static VehicleCategoryDetails CategoryDetails(string code = "ECMR", FuelType fuel = FuelType.Petrol) => new(
        code,
        "Économique",
        "Peugeot 208",
        Seats: 5,
        Doors: 5,
        LargeBags: 1,
        SmallBags: 2,
        fuel,
        Deposit: Money.Tnd(600m),
        ReducedDeposit: Money.Tnd(300m),
        DisplayOrder: 2);
}
