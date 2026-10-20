using CarRental.Application.Fleet.Contracts;
using CarRental.Domain.Common;
using CarRental.Domain.Fleet;

namespace CarRental.Application.Tests.Fleet;

/// <summary>Contract semantics on test doubles (the same semantics run against SQL Server in the integration tests).</summary>
public sealed class FleetQueriesTests
{
    private readonly FleetTestBed _bed = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Period Range(int fromDays, int toDays) =>
        Period.Create(FleetTestBed.Now.AddDays(fromDays), FleetTestBed.Now.AddDays(toDays)).Value;

    [Fact]
    public async Task Categories_are_ordered_by_display_order_then_code_and_inactive_ones_are_optional()
    {
        _bed.AddCategory("IFMR", FuelType.Diesel, displayOrder: 5);
        _bed.AddCategory("CDMR", FuelType.Diesel, displayOrder: 4);
        _bed.AddCategory("ECMR", displayOrder: 4);
        _bed.AddCategory("MCMR", displayOrder: 1, active: false);

        var active = await _bed.Queries().GetCategoriesAsync(_bed.AgencyId, cancellationToken: Ct);
        var all = await _bed.Queries().GetCategoriesAsync(includeInactive: true, cancellationToken: Ct);
        var other = await _bed.Queries().GetCategoriesAsync(Guid.CreateVersion7(), includeInactive: true, Ct);

        active.Select(c => c.AcrissCode).ShouldBe(["CDMR", "ECMR", "IFMR"]);
        all.Select(c => c.AcrissCode).ShouldBe(["MCMR", "CDMR", "ECMR", "IFMR"]);
        other.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_category_exposes_its_deposits_and_derived_features()
    {
        var category = _bed.AddCategory("EDAR");

        var info = (await _bed.Queries().GetCategoryAsync(category.Id, Ct)).ShouldNotBeNull();

        info.AcrissCode.ShouldBe("EDAR");
        info.Transmission.ShouldBe(TransmissionInfo.Automatic);
        info.AirConditioning.ShouldBeTrue();
        info.Deposit.ShouldBe(Money.Tnd(600m));
        info.ReducedDeposit.ShouldBe(Money.Tnd(300m));
        info.PhotoUrl.ShouldBeNull();
        (await _bed.Queries().GetCategoryAsync(Guid.CreateVersion7(), Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task A_category_is_found_by_its_acriss_code_when_active()
    {
        var category = _bed.AddCategory("ECMR");
        _bed.AddCategory("MCMR", active: false);

        (await _bed.Queries().FindCategoryByCodeAsync(_bed.AgencyId, " ecmr ", Ct))!.Id.ShouldBe(category.Id);
        (await _bed.Queries().FindCategoryByCodeAsync(_bed.AgencyId, "MCMR", Ct)).ShouldBeNull();
        (await _bed.Queries().FindCategoryByCodeAsync(_bed.AgencyId, "not a code", Ct)).ShouldBeNull();
        (await _bed.Queries().FindCategoryByCodeAsync(Guid.CreateVersion7(), "ECMR", Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task Vehicles_of_a_category_exclude_sold_ones_and_are_ordered_by_plate()
    {
        var category = _bed.AddCategory();
        var second = _bed.AddVehicle(category, "220 TU 1000");
        var first = _bed.AddVehicle(category, "210 TU 1000");
        var sold = _bed.AddVehicle(category, "205 TU 1000");
        sold.Sell(FleetTestBed.Now, Money.Tnd(1m), null, FleetTestBed.Stamp()).IsSuccess.ShouldBeTrue();
        var maintenance = _bed.AddVehicle(category, "230 TU 1000");
        maintenance.ChangeStatus(VehicleStatus.Maintenance, "Révision", FleetTestBed.Stamp()).IsSuccess.ShouldBeTrue();

        var vehicles = await _bed.Queries().GetVehiclesAsync(category.Id, Ct);

        vehicles.Select(v => v.Id).ShouldBe([first.Id, second.Id, maintenance.Id]);
        vehicles[2].Status.ShouldBe(VehicleStatusInfo.Maintenance);
        (await _bed.Queries().GetVehicleAsync(sold.Id, Ct))!.Status.ShouldBe(VehicleStatusInfo.Sold);
    }

    [Fact]
    public async Task A_vehicle_exposes_the_end_of_its_compliance()
    {
        var category = _bed.AddCategory();
        var vehicle = _bed.AddVehicle(category, documentsExpireOn: new DateOnly(2027, 6, 30));
        vehicle.SetGuaranteedModelSupplement(Money.Tnd(20m), FleetTestBed.Stamp()).IsSuccess.ShouldBeTrue();
        vehicle.RecordDocument(new VehicleDocumentDetails(VehicleDocumentType.RoadTax, "VIG", null, new DateOnly(2027, 1, 31)), FleetTestBed.Stamp()).IsSuccess.ShouldBeTrue();

        var info = (await _bed.Queries().GetVehicleAsync(vehicle.Id, Ct)).ShouldNotBeNull();

        info.CompliantUntil.ShouldBe(VehicleCompliance.EndOfTunisDay(new DateOnly(2027, 1, 31)));
        info.GuaranteedModelDailySupplement.ShouldBe(Money.Tnd(20m));
        info.IsCompliantFor(Period.Create(FleetTestBed.Now, info.CompliantUntil!.Value).Value).ShouldBeTrue();
        info.IsCompliantFor(Period.Create(FleetTestBed.Now, info.CompliantUntil!.Value.AddTicks(1)).Value).ShouldBeFalse();
        (await _bed.Queries().GetVehicleAsync(Guid.CreateVersion7(), Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task Availability_lists_every_non_sold_vehicle_with_its_windows_clipped_to_the_range()
    {
        var category = _bed.AddCategory();
        var free = _bed.AddVehicle(category, "210 TU 1000");
        var blocked = _bed.AddVehicle(category, "211 TU 1000");
        var block = Period.Create(FleetTestBed.Now.AddDays(2), FleetTestBed.Now.AddDays(3)).Value;
        blocked.ScheduleMaintenance(new MaintenanceDetails(MaintenanceType.Brakes, block.Start, block.End), FleetTestBed.Stamp()).IsSuccess.ShouldBeTrue();
        var nonCompliant = _bed.AddVehicle(category, "212 TU 1000", documentsExpireOn: new DateOnly(2026, 10, 1));
        var outOfService = _bed.AddVehicle(category, "213 TU 1000");
        outOfService.ChangeStatus(VehicleStatus.OutOfService, "Accident", FleetTestBed.Stamp()).IsSuccess.ShouldBeTrue();
        var sold = _bed.AddVehicle(category, "209 TU 1000");
        sold.Sell(FleetTestBed.Now.AddDays(-1), Money.Tnd(1m), null, FleetTestBed.Stamp()).IsSuccess.ShouldBeTrue();
        var range = Range(1, 5);

        var availability = await _bed.Queries().GetAvailabilityAsync(category.Id, range, Ct);

        availability.Select(a => a.VehicleId).ShouldBe([free.Id, blocked.Id, nonCompliant.Id, outOfService.Id]);
        availability[0].Windows.ShouldBe([range]);
        availability[1].Windows.ShouldBe([Period.Create(range.Start, block.Start).Value, Period.Create(block.End, range.End).Value]);
        availability[2].Windows.ShouldBeEmpty();
        availability[3].Windows.ShouldBeEmpty();
        availability[1].PlateNumber.ShouldBe("211 TU 1000");
        (await _bed.Queries().GetAvailabilityAsync(Guid.CreateVersion7(), range, Ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Deadlines_list_the_current_documents_of_a_vehicle()
    {
        var vehicle = _bed.AddVehicle(_bed.AddCategory(), documentsExpireOn: new DateOnly(2027, 6, 30));
        vehicle.RecordDocument(new VehicleDocumentDetails(VehicleDocumentType.RegistrationCard, "CG-1", null, null), FleetTestBed.Stamp()).IsSuccess.ShouldBeTrue();
        vehicle.RecordDocument(new VehicleDocumentDetails(VehicleDocumentType.Insurance, "POL-NEW", null, new DateOnly(2028, 6, 30)), FleetTestBed.Stamp()).IsSuccess.ShouldBeTrue();

        var deadlines = await _bed.Queries().GetDeadlinesAsync(vehicle.Id, Ct);

        deadlines.Select(d => d.Type).ShouldBe([VehicleDocumentTypeInfo.Insurance, VehicleDocumentTypeInfo.RoadTax, VehicleDocumentTypeInfo.TechnicalInspection, VehicleDocumentTypeInfo.RegistrationCard]);
        deadlines[0].Number.ShouldBe("POL-NEW");
        deadlines[0].ValidUntil.ShouldBe(VehicleCompliance.EndOfTunisDay(new DateOnly(2028, 6, 30)));
        deadlines[3].IsMandatory.ShouldBeFalse();
        deadlines[3].ExpiresOn.ShouldBeNull();
        deadlines[3].ValidUntil.ShouldBeNull();
        (await _bed.Queries().GetDeadlinesAsync(Guid.CreateVersion7(), Ct)).ShouldBeEmpty();
    }

    [Fact]
    public void Contract_enums_mirror_the_domain_enums()
    {
        Enum.GetValues<TransmissionInfo>().Select(v => (int)v).ShouldBe(Enum.GetValues<Transmission>().Select(v => (int)v));
        Enum.GetNames<TransmissionInfo>().ShouldBe(Enum.GetNames<Transmission>());
        Enum.GetNames<FuelTypeInfo>().ShouldBe(Enum.GetNames<FuelType>());
        Enum.GetValues<FuelTypeInfo>().Select(v => (int)v).ShouldBe(Enum.GetValues<FuelType>().Select(v => (int)v));
        Enum.GetNames<VehicleStatusInfo>().ShouldBe(Enum.GetNames<VehicleStatus>());
        Enum.GetValues<VehicleStatusInfo>().Select(v => (int)v).ShouldBe(Enum.GetValues<VehicleStatus>().Select(v => (int)v));
        Enum.GetNames<VehicleDocumentTypeInfo>().ShouldBe(Enum.GetNames<VehicleDocumentType>());
        Enum.GetValues<VehicleDocumentTypeInfo>().Select(v => (int)v).ShouldBe(Enum.GetValues<VehicleDocumentType>().Select(v => (int)v));
    }
}
