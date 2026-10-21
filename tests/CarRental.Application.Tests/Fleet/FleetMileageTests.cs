using CarRental.Application.Fleet;
using CarRental.Application.Identity.Contracts;
using CarRental.Domain.Fleet;

namespace CarRental.Application.Tests.Fleet;

/// <summary>The fleet contract of the hand-overs: the reading of a rental file never lowers the mileage and is traced.</summary>
public sealed class FleetMileageTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FleetTestBed _bed = new();

    private FleetMileage Mileage(string role)
    {
        var user = FleetTestBed.As(role);
        return new FleetMileage(_bed.Writer(user), user);
    }

    [Fact]
    public void The_contract_is_registered_by_the_fleet_module()
    {
        typeof(FleetModule).Assembly.GetType("CarRental.Application.Fleet.FleetMileage").ShouldNotBeNull()
            .GetInterfaces().ShouldContain(typeof(CarRental.Application.Fleet.Contracts.IFleetMileage));
    }

    [Fact]
    public async Task A_hand_over_records_the_reading_with_the_number_of_the_rental_file()
    {
        var vehicle = _bed.AddVehicle(_bed.AddCategory(), mileage: 20_000);

        (await Mileage(Roles.Preparer).RecordRentalMileageAsync(vehicle.Id, 20_850, Guid.CreateVersion7(), "LOC-2027-000123", Ct)).IsSuccess.ShouldBeTrue();

        vehicle.Mileage.ShouldBe(20_850);
        var line = vehicle.History.Where(h => h.Kind == VehicleHistoryKind.MileageRecorded).ShouldHaveSingleItem();
        (line.Kind, line.PreviousValue, line.NewValue, line.Reason).ShouldBe((VehicleHistoryKind.MileageRecorded, "20000", "20850", "LOC-2027-000123"));
        _bed.Lock.Acquired.ShouldBeEmpty("a reading does not change the availability");
    }

    [Fact]
    public async Task The_mileage_never_decreases_and_an_equal_reading_changes_nothing()
    {
        var vehicle = _bed.AddVehicle(_bed.AddCategory(), mileage: 20_000);
        var mileage = Mileage(Roles.Agent);

        (await mileage.RecordRentalMileageAsync(vehicle.Id, 19_999, Guid.CreateVersion7(), "LOC-2027-000124", Ct)).Error.ShouldBe(FleetErrors.MileageDecrease);
        (await mileage.RecordRentalMileageAsync(vehicle.Id, 20_000, Guid.CreateVersion7(), "LOC-2027-000124", Ct)).IsSuccess.ShouldBeTrue();

        vehicle.Mileage.ShouldBe(20_000);
        vehicle.History.ShouldNotContain(h => h.Kind == VehicleHistoryKind.MileageRecorded);
    }

    [Fact]
    public async Task Only_hand_over_or_fleet_staff_record_a_reading()
    {
        var vehicle = _bed.AddVehicle(_bed.AddCategory(), mileage: 20_000);

        (await Mileage(Roles.Accountant).RecordRentalMileageAsync(vehicle.Id, 21_000, Guid.CreateVersion7(), "LOC-2027-000125", Ct)).Error.ShouldBe(FleetErrors.Forbidden);
        (await Mileage(Roles.Manager).RecordRentalMileageAsync(Guid.CreateVersion7(), 21_000, Guid.CreateVersion7(), "LOC-2027-000125", Ct)).Error.ShouldBe(FleetErrors.VehicleNotFound);
        vehicle.Mileage.ShouldBe(20_000);
    }
}
