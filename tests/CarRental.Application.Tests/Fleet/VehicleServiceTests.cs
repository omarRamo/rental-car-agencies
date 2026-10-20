using CarRental.Application.Common.Concurrency;
using CarRental.Application.Common.Persistence;
using CarRental.Application.Fleet;
using CarRental.Application.Identity.Contracts;
using CarRental.Domain.Common;
using CarRental.Domain.Fleet;
using Microsoft.EntityFrameworkCore;

namespace CarRental.Application.Tests.Fleet;

public sealed class VehicleServiceTests
{
    private readonly FleetTestBed _bed = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(Roles.Agent)]
    [InlineData(Roles.Accountant)]
    [InlineData(Roles.Preparer)]
    public async Task Only_fleet_managers_register_and_edit_vehicles(string role)
    {
        var category = _bed.AddCategory();
        var vehicle = _bed.AddVehicle(category);
        var service = _bed.Vehicles(role);

        (await service.RegisterAsync(FleetTestBed.Form(category.Id, "300 TU 1"), 10, Ct)).Error.ShouldBe(FleetErrors.Forbidden);
        (await service.UpdateAsync(vehicle.Id, FleetTestBed.Form(category.Id), Ct)).Error.ShouldBe(FleetErrors.Forbidden);
        (await service.SellAsync(vehicle.Id, new SaleForm(FleetTestBed.Now, 1m, null), Ct)).Error.ShouldBe(FleetErrors.Forbidden);
        (await service.RecordDocumentAsync(vehicle.Id, new DocumentForm(VehicleDocumentType.Insurance, "P", null, new DateOnly(2028, 1, 1), null, null, null), Ct))
            .Error.ShouldBe(FleetErrors.Forbidden);
        (await service.CorrectMileageAsync(vehicle.Id, 1, "x", Ct)).Error.ShouldBe(FleetErrors.Forbidden);
        (await service.SetEconomicsAsync(vehicle.Id, new EconomicsForm(1m, new DateOnly(2024, 1, 1), FinancingType.Cash, null, null, 5, 0m), Ct)).Error.ShouldBe(FleetErrors.Forbidden);
        _bed.Db.Fake<Vehicle>().Items.ShouldHaveSingleItem();
        _bed.Notifier.Changed.ShouldBeEmpty();
    }

    [Fact]
    public async Task Registering_a_vehicle_locks_and_signals_its_category()
    {
        var category = _bed.AddCategory();

        var result = await _bed.Vehicles().RegisterAsync(FleetTestBed.Form(category.Id, " 230 tu 7777 ") with { GuaranteedModelDailySupplement = 15m }, 12_000, Ct);

        result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error.ToString() : null);
        var vehicle = _bed.Db.Fake<Vehicle>().Items.ShouldHaveSingleItem();
        vehicle.Id.ShouldBe(result.Value);
        vehicle.PlateNumber.ShouldBe("230 TU 7777");
        vehicle.AgencyId.ShouldBe(_bed.AgencyId);
        vehicle.Mileage.ShouldBe(12_000);
        vehicle.GuaranteedModelDailySupplement.ShouldBe(Money.Tnd(15m));
        vehicle.History[0].Actor.ShouldBe("manager@demo.local");
        _bed.Lock.Acquired.ShouldBe([category.Id]);
        _bed.Notifier.Changed.ShouldBe([category.Id]);
    }

    [Fact]
    public async Task A_plate_is_unique_within_the_agency()
    {
        var category = _bed.AddCategory();
        _bed.AddVehicle(category, "215 TU 4512");

        var result = await _bed.Vehicles().RegisterAsync(FleetTestBed.Form(category.Id, "215  tu 4512"), 0, Ct);

        result.Error.ShouldBe(FleetErrors.PlateNumberTaken);
        _bed.Notifier.Changed.ShouldBeEmpty();
    }

    [Fact]
    public async Task Registration_errors_are_reported_without_signal()
    {
        var category = _bed.AddCategory();

        (await _bed.Vehicles().RegisterAsync(FleetTestBed.Form(Guid.CreateVersion7()), 0, Ct)).Error.ShouldBe(FleetErrors.CategoryNotFound);
        (await _bed.Vehicles().RegisterAsync(FleetTestBed.Form(category.Id, "?"), 0, Ct)).Error.ShouldBe(FleetErrors.PlateNumberInvalid);
        (await _bed.Vehicles().RegisterAsync(FleetTestBed.Form(category.Id) with { GuaranteedModelDailySupplement = 1.0001m }, 0, Ct)).Error.ShouldBe(FleetErrors.AmountInvalid);
        (await _bed.Vehicles().RegisterAsync(FleetTestBed.Form(category.Id), -1, Ct)).Error.ShouldBe(FleetErrors.MileageInvalid);
        _bed.Notifier.Changed.ShouldBeEmpty();
    }

    [Fact]
    public async Task Moving_a_vehicle_to_another_category_locks_both_in_id_order_and_signals_both()
    {
        var first = _bed.AddCategory("ECMR");
        var second = _bed.AddCategory("CDMR", FuelType.Diesel);
        var vehicle = _bed.AddVehicle(first);

        var result = await _bed.Vehicles().ChangeCategoryAsync(vehicle.Id, second.Id, Ct);

        result.IsSuccess.ShouldBeTrue();
        vehicle.CategoryId.ShouldBe(second.Id);
        _bed.Lock.Acquired.ShouldBe(new[] { first.Id, second.Id }.Order());
        _bed.Notifier.Changed.ShouldBe([first.Id, second.Id], ignoreOrder: true);
    }

    [Fact]
    public async Task Editing_the_identity_can_move_the_vehicle_and_keeps_the_plate_unique()
    {
        var first = _bed.AddCategory("ECMR");
        var second = _bed.AddCategory("EDAR");
        var vehicle = _bed.AddVehicle(first, "215 TU 4512");
        _bed.AddVehicle(first, "216 TU 1000");

        (await _bed.Vehicles().UpdateAsync(vehicle.Id, FleetTestBed.Form(first.Id, "216 TU 1000"), Ct)).Error.ShouldBe(FleetErrors.PlateNumberTaken);
        var moved = await _bed.Vehicles().UpdateAsync(vehicle.Id, FleetTestBed.Form(second.Id, "215 TU 4512") with { Color = "Gris" }, Ct);

        moved.IsSuccess.ShouldBeTrue(moved.IsFailure ? moved.Error.ToString() : null);
        vehicle.CategoryId.ShouldBe(second.Id);
        vehicle.Color.ShouldBe("Gris");
        _bed.Notifier.Changed.ShouldBe([first.Id, second.Id], ignoreOrder: true);
    }

    [Fact]
    public async Task A_category_of_another_agency_is_refused()
    {
        var category = _bed.AddCategory();
        var vehicle = _bed.AddVehicle(category);
        var foreign = VehicleCategory.Create(
            Guid.CreateVersion7(),
            new VehicleCategoryDetails("CDMR", "Autre", "Golf", 5, 5, 2, 2, FuelType.Diesel, Money.Tnd(800m), Money.Tnd(400m))).Value;
        _bed.Db.Fake<VehicleCategory>().Add(foreign);

        (await _bed.Vehicles().ChangeCategoryAsync(vehicle.Id, foreign.Id, Ct)).Error.ShouldBe(FleetErrors.CategoryAgencyMismatch);
        (await _bed.Vehicles().ChangeCategoryAsync(vehicle.Id, Guid.CreateVersion7(), Ct)).Error.ShouldBe(FleetErrors.CategoryNotFound);
        (await _bed.Vehicles().ChangeCategoryAsync(Guid.CreateVersion7(), category.Id, Ct)).Error.ShouldBe(FleetErrors.VehicleNotFound);
    }

    [Fact]
    public async Task A_status_change_is_signalled_and_audited_in_the_history()
    {
        var category = _bed.AddCategory();
        var vehicle = _bed.AddVehicle(category);

        var result = await _bed.Vehicles().ChangeStatusAsync(vehicle.Id, VehicleStatus.OutOfService, "Sinistre", Ct);

        result.IsSuccess.ShouldBeTrue();
        vehicle.Status.ShouldBe(VehicleStatus.OutOfService);
        _bed.Lock.Acquired.ShouldBe([category.Id]);
        _bed.Notifier.Changed.ShouldBe([category.Id]);
        vehicle.History[^1].Kind.ShouldBe(VehicleHistoryKind.StatusChanged);
    }

    [Fact]
    public async Task A_preparer_moves_a_vehicle_between_active_and_maintenance_only()
    {
        var category = _bed.AddCategory();
        var vehicle = _bed.AddVehicle(category);
        var preparer = _bed.Vehicles(Roles.Preparer);

        (await preparer.ChangeStatusAsync(vehicle.Id, VehicleStatus.Maintenance, "Bruit de freins", Ct)).IsSuccess.ShouldBeTrue();
        (await preparer.ChangeStatusAsync(vehicle.Id, VehicleStatus.OutOfService, "Accident", Ct)).Error.ShouldBe(FleetErrors.Forbidden);
        (await preparer.ChangeStatusAsync(vehicle.Id, VehicleStatus.Active, null, Ct)).IsSuccess.ShouldBeTrue();
        (await preparer.RecordMileageAsync(vehicle.Id, 21_000, Ct)).IsSuccess.ShouldBeTrue();
        (await _bed.Vehicles(Roles.Agent).RecordMileageAsync(vehicle.Id, 22_000, Ct)).Error.ShouldBe(FleetErrors.Forbidden);

        vehicle.Mileage.ShouldBe(21_000);
        vehicle.Status.ShouldBe(VehicleStatus.Active);
    }

    [Fact]
    public async Task Recording_the_mileage_does_not_touch_the_availability()
    {
        var category = _bed.AddCategory();
        var vehicle = _bed.AddVehicle(category);

        (await _bed.Vehicles().RecordMileageAsync(vehicle.Id, 25_000, Ct)).IsSuccess.ShouldBeTrue();
        (await _bed.Vehicles().RecordMileageAsync(vehicle.Id, 24_000, Ct)).Error.ShouldBe(FleetErrors.MileageDecrease);
        (await _bed.Vehicles().CorrectMileageAsync(vehicle.Id, 24_000, "Erreur de saisie", Ct)).IsSuccess.ShouldBeTrue();

        vehicle.Mileage.ShouldBe(24_000);
        _bed.Lock.Acquired.ShouldBeEmpty();
        _bed.Notifier.Changed.ShouldBeEmpty();
    }

    [Fact]
    public async Task Recording_a_mandatory_document_changes_the_compliance_and_is_signalled()
    {
        var category = _bed.AddCategory();
        var vehicle = _bed.AddVehicle(category);

        var result = await _bed.Vehicles().RecordDocumentAsync(
            vehicle.Id,
            new DocumentForm(VehicleDocumentType.Insurance, "POL-2028", new DateOnly(2027, 7, 1), new DateOnly(2028, 6, 30), "Assureur", 1_250.5m, null),
            Ct);

        result.IsSuccess.ShouldBeTrue();
        vehicle.CurrentDocument(VehicleDocumentType.Insurance)!.Id.ShouldBe(result.Value);
        vehicle.CurrentDocument(VehicleDocumentType.Insurance)!.Cost.ShouldBe(Money.Tnd(1_250.5m));
        _bed.Notifier.Changed.ShouldBe([category.Id]);
        (await _bed.Vehicles().RecordDocumentAsync(vehicle.Id, new DocumentForm(VehicleDocumentType.Insurance, "P", null, null, null, null, null), Ct))
            .Error.ShouldBe(FleetErrors.DocumentExpiryRequired);
        (await _bed.Vehicles().RecordDocumentAsync(vehicle.Id, new DocumentForm(VehicleDocumentType.Insurance, "P", null, new DateOnly(2028, 1, 1), null, -1m, null), Ct))
            .Error.ShouldBe(FleetErrors.AmountInvalid);
    }

    [Fact]
    public async Task Correcting_a_document_is_signalled()
    {
        var category = _bed.AddCategory();
        var vehicle = _bed.AddVehicle(category);
        var roadTax = vehicle.CurrentDocument(VehicleDocumentType.RoadTax)!;

        var result = await _bed.Vehicles().CorrectDocumentAsync(
            vehicle.Id,
            roadTax.Id,
            new DocumentForm(VehicleDocumentType.RoadTax, "VIG-OK", null, new DateOnly(2027, 3, 31), null, null, null),
            Ct);

        result.IsSuccess.ShouldBeTrue();
        roadTax.Number.ShouldBe("VIG-OK");
        _bed.Notifier.Changed.ShouldBe([category.Id]);
    }

    [Fact]
    public async Task A_sale_is_signalled_and_the_vehicle_leaves_the_category()
    {
        var category = _bed.AddCategory();
        var vehicle = _bed.AddVehicle(category);

        (await _bed.Vehicles().SellAsync(vehicle.Id, new SaleForm(FleetTestBed.Now.AddHours(-1), 31_500m, "Particulier"), Ct)).IsSuccess.ShouldBeTrue();
        (await _bed.Vehicles().SellAsync(vehicle.Id, new SaleForm(FleetTestBed.Now, -1m, null), Ct)).Error.ShouldBe(FleetErrors.AmountInvalid);

        vehicle.Status.ShouldBe(VehicleStatus.Sold);
        _bed.Notifier.Changed.ShouldBe([category.Id]);
        (await _bed.Queries().GetVehiclesAsync(category.Id, Ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Economics_and_costs_are_recorded_without_signal()
    {
        var category = _bed.AddCategory();
        var vehicle = _bed.AddVehicle(category);

        (await _bed.Vehicles().SetEconomicsAsync(vehicle.Id, new EconomicsForm(55_000m, new DateOnly(2024, 3, 1), FinancingType.Leasing, 1_350m, 36, 5, 16_500m), Ct)).IsSuccess.ShouldBeTrue();
        (await _bed.Vehicles().SetEconomicsAsync(vehicle.Id, new EconomicsForm(55_000m, new DateOnly(2024, 3, 1), FinancingType.Leasing, null, 36, 5, 16_500m), Ct)).Error.ShouldBe(FleetErrors.FinancingInvalid);
        (await _bed.Vehicles().SetEconomicsAsync(vehicle.Id, new EconomicsForm(-5m, new DateOnly(2024, 3, 1), FinancingType.Cash, null, null, 5, 0m), Ct)).Error.ShouldBe(FleetErrors.PurchasePriceInvalid);
        var cost = await _bed.Vehicles().AddCostAsync(vehicle.Id, new CostForm(new DateOnly(2026, 10, 1), "Traceur GPS", 380m), Ct);
        (await _bed.Vehicles().AddCostAsync(vehicle.Id, new CostForm(new DateOnly(2026, 10, 1), "Arrondi", 1.0005m), Ct)).Error.ShouldBe(FleetErrors.CostLabelInvalid);

        vehicle.Economics!.Financing.ShouldBe(FinancingType.Leasing);
        vehicle.Costs.ShouldHaveSingleItem().Id.ShouldBe(cost.Value);
        (await _bed.Vehicles().RemoveCostAsync(vehicle.Id, cost.Value, Ct)).IsSuccess.ShouldBeTrue();
        vehicle.Costs.ShouldBeEmpty();
        _bed.Notifier.Changed.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_busy_category_lock_is_reported_and_nothing_is_signalled()
    {
        var category = _bed.AddCategory();
        var vehicle = _bed.AddVehicle(category);
        _bed.Lock.FailWith = AvailabilityLockErrors.Timeout;

        var result = await _bed.Vehicles().ChangeStatusAsync(vehicle.Id, VehicleStatus.Maintenance, "Révision", Ct);

        result.Error.ShouldBe(FleetErrors.AvailabilityBusy);
        _bed.Notifier.Changed.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_concurrent_update_becomes_a_business_error()
    {
        var category = _bed.AddCategory();
        var vehicle = _bed.AddVehicle(category);
        _bed.UnitOfWork = new ConcurrencyConflictUnitOfWork();

        var result = await _bed.Vehicles().RecordMileageAsync(vehicle.Id, 30_000, Ct);

        result.Error.ShouldBe(FleetErrors.ConcurrentUpdate);
        _bed.Notifier.Changed.ShouldBeEmpty();
    }

    [Fact]
    public async Task Unknown_vehicles_are_reported()
    {
        (await _bed.Vehicles().RecordMileageAsync(Guid.CreateVersion7(), 1, Ct)).Error.ShouldBe(FleetErrors.VehicleNotFound);
        (await _bed.Vehicles().SellAsync(Guid.CreateVersion7(), new SaleForm(FleetTestBed.Now, 1m, null), Ct)).Error.ShouldBe(FleetErrors.VehicleNotFound);
    }

    /// <summary>Simulates the rowversion check failing at commit.</summary>
    private sealed class ConcurrencyConflictUnitOfWork : IUnitOfWork
    {
        public bool HasActiveTransaction => false;

        public Task<Result> ExecuteAsync(Func<CancellationToken, Task<Result>> operation, System.Data.IsolationLevel isolationLevel = System.Data.IsolationLevel.ReadCommitted, CancellationToken cancellationToken = default) =>
            throw new DbUpdateConcurrencyException("Simulated concurrent update.");

        public Task<Result<T>> ExecuteAsync<T>(Func<CancellationToken, Task<Result<T>>> operation, System.Data.IsolationLevel isolationLevel = System.Data.IsolationLevel.ReadCommitted, CancellationToken cancellationToken = default) =>
            throw new DbUpdateConcurrencyException("Simulated concurrent update.");
    }
}
