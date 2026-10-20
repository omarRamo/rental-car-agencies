using CarRental.Application.Common.Persistence;
using CarRental.Application.Fleet;
using CarRental.Domain.Common;
using CarRental.Domain.Fleet;
using CarRental.Domain.Identity;
using CarRental.Infrastructure.Outbox;
using CarRental.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CarRental.Integration.Tests.Fleet;

/// <summary>The fleet model on SQL Server: round-trip, temporal tables, unique plates, optimistic concurrency, audit and outbox.</summary>
public sealed class FleetPersistenceTests(IntegrationHost host)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_vehicle_and_all_its_parts_round_trip()
    {
        var agencyId = await FleetData.NewAgencyAsync(host, Ct);
        var categoryId = await FleetData.NewCategoryAsync(host, agencyId, "CDMR", Ct);
        var vehicleId = await FleetData.NewVehicleAsync(host, categoryId, Ct, documentsExpireOn: new DateOnly(2031, 3, 31));
        await using (var scope = host.CreateScope(FleetData.Manager))
        {
            var vehicles = scope.ServiceProvider.GetRequiredService<VehicleService>();
            var maintenance = scope.ServiceProvider.GetRequiredService<VehicleMaintenanceService>();
            var start = DateTimeOffset.UtcNow.AddDays(10);
            (await vehicles.SetEconomicsAsync(vehicleId, new EconomicsForm(78_500.250m, new DateOnly(2025, 1, 15), FinancingType.Credit, 1_725.125m, 48, 5, 23_550m), Ct)).IsSuccess.ShouldBeTrue();
            (await vehicles.AddCostAsync(vehicleId, new CostForm(new DateOnly(2025, 2, 1), "Traceur GPS", 349.990m), Ct)).IsSuccess.ShouldBeTrue();
            (await maintenance.SetPlanAsync(vehicleId, new MaintenancePlanForm(MaintenanceType.OilChange, 10_000, 12, new DateOnly(2025, 1, 15), 15_000, "Vidange"), Ct)).IsSuccess.ShouldBeTrue();
            (await maintenance.ScheduleAsync(vehicleId, new MaintenanceForm(MaintenanceType.Brakes, start, start.AddHours(6), "Plaquettes", "Garage", 410.5m), Ct)).IsSuccess.ShouldBeTrue();
        }

        await using var check = host.CreateScope(FleetData.Manager);
        var detail = (await check.ServiceProvider.GetRequiredService<FleetAdminQueries>().GetVehicleAsync(vehicleId, Ct)).Value;

        detail.CategoryCode.ShouldBe("CDMR");
        detail.Mileage.ShouldBe(15_000);
        detail.InServiceOn.ShouldBe(new DateOnly(2025, 1, 15));
        detail.FirstRegistrationOn.ShouldBe(new DateOnly(2025, 1, 10));
        detail.CompliantUntil.ShouldBe(VehicleCompliance.EndOfTunisDay(new DateOnly(2031, 3, 31)));
        detail.Documents.Count.ShouldBe(3);
        detail.Economics.ShouldNotBeNull().PurchasePrice.ShouldBe(Money.Tnd(78_500.250m));
        detail.Economics.MonthlyPayment.ShouldBe(Money.Tnd(1_725.125m));
        detail.Economics.Financing.ShouldBe(FinancingType.Credit);
        detail.Costs.ShouldHaveSingleItem().Amount.ShouldBe(Money.Tnd(349.990m));
        detail.MaintenancePlans.ShouldHaveSingleItem().Label.ShouldBe("Vidange");
        var block = detail.Maintenances.ShouldHaveSingleItem();
        block.Cost.ShouldBe(Money.Tnd(410.5m));
        (block.EndsAtUtc - block.StartsAtUtc).ShouldBe(TimeSpan.FromHours(6));
        detail.History.Count.ShouldBeGreaterThanOrEqualTo(8);
        detail.History[0].Kind.ShouldBe(VehicleHistoryKind.MaintenanceScheduled);
        detail.History[^1].Kind.ShouldBe(VehicleHistoryKind.Registered);
        detail.History.ShouldAllBe(h => h.Actor == "gerant@demo.local");
    }

    [Fact]
    public async Task Vehicles_and_categories_keep_their_previous_versions_in_temporal_tables()
    {
        var agencyId = await FleetData.NewAgencyAsync(host, Ct);
        var categoryId = await FleetData.NewCategoryAsync(host, agencyId, "ECMR", Ct);
        var vehicleId = await FleetData.NewVehicleAsync(host, categoryId, Ct, withDocuments: false);
        await using (var scope = host.CreateScope(FleetData.Manager))
        {
            (await scope.ServiceProvider.GetRequiredService<VehicleService>().RecordMileageAsync(vehicleId, 16_000, Ct)).IsSuccess.ShouldBeTrue();
            (await scope.ServiceProvider.GetRequiredService<VehicleCategoryService>().SetActiveAsync(categoryId, false, Ct)).IsSuccess.ShouldBeTrue();
        }

        await using var check = host.CreateScope();
        var db = check.ServiceProvider.GetRequiredService<CarRentalDbContext>();
        var mileages = await db.Set<Vehicle>().TemporalAll().Where(v => v.Id == vehicleId).Select(v => v.Mileage).ToListAsync(Ct);
        var states = await db.Set<VehicleCategory>().TemporalAll().Where(c => c.Id == categoryId).Select(c => c.IsActive).ToListAsync(Ct);

        mileages.ShouldContain(15_000);
        mileages.ShouldContain(16_000);
        states.ShouldContain(true);
        states.ShouldContain(false);
    }

    [Fact]
    public async Task A_plate_is_unique_within_an_agency_also_in_the_database()
    {
        var agencyId = await FleetData.NewAgencyAsync(host, Ct);
        var categoryId = await FleetData.NewCategoryAsync(host, agencyId, "MCMR", Ct);
        var plate = FleetData.NewPlate();
        await FleetData.NewVehicleAsync(host, categoryId, Ct, plate: plate);

        await using (var scope = host.CreateScope(FleetData.Manager))
        {
            var duplicate = await scope.ServiceProvider.GetRequiredService<VehicleService>().RegisterAsync(FleetData.Form(categoryId, plate.ToLowerInvariant()), 0, Ct);
            duplicate.Error.ShouldBe(FleetErrors.PlateNumberTaken);
        }

        await using var direct = host.CreateScope();
        var db = direct.ServiceProvider.GetRequiredService<IAppDbContext>();
        db.Set<Vehicle>().Add(Vehicle.Register(agencyId, categoryId, FleetData.Form(categoryId, plate).ToDetails(), 0, new ChangeStamp(DateTimeOffset.UtcNow, "tests")).Value);
        await Should.ThrowAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));

        var otherAgency = await FleetData.NewAgencyAsync(host, Ct);
        var otherCategory = await FleetData.NewCategoryAsync(host, otherAgency, "MCMR", Ct);
        await FleetData.NewVehicleAsync(host, otherCategory, Ct, plate: plate);
    }

    [Fact]
    public async Task Two_concurrent_edits_of_the_same_vehicle_cannot_both_succeed()
    {
        var agencyId = await FleetData.NewAgencyAsync(host, Ct);
        var categoryId = await FleetData.NewCategoryAsync(host, agencyId, "ECMR", Ct);
        var vehicleId = await FleetData.NewVehicleAsync(host, categoryId, Ct);

        await using var stale = host.CreateScope();
        var staleDb = stale.ServiceProvider.GetRequiredService<CarRentalDbContext>();
        var staleVehicle = await staleDb.Set<Vehicle>().Include(v => v.Costs).FirstAsync(v => v.Id == vehicleId, Ct);
        staleVehicle.AddCost(new DateOnly(2025, 3, 1), "Lavage", Money.Tnd(30m), new ChangeStamp(DateTimeOffset.UtcNow, "stale")).IsSuccess.ShouldBeTrue();

        await using (var scope = host.CreateScope(FleetData.Manager))
        {
            (await scope.ServiceProvider.GetRequiredService<VehicleService>().RecordMileageAsync(vehicleId, 15_500, Ct)).IsSuccess.ShouldBeTrue();
        }

        await Should.ThrowAsync<DbUpdateConcurrencyException>(() => staleDb.SaveChangesAsync(Ct));
    }

    [Fact]
    public async Task Fleet_changes_are_audited_and_capacity_events_reach_the_outbox()
    {
        var agencyId = await FleetData.NewAgencyAsync(host, Ct);
        var categoryId = await FleetData.NewCategoryAsync(host, agencyId, "IFMR", Ct);
        var vehicleId = await FleetData.NewVehicleAsync(host, categoryId, Ct);

        await using var check = host.CreateScope();
        var db = check.ServiceProvider.GetRequiredService<CarRentalDbContext>();
        var audit = await db.Set<AuditLog>().Where(a => a.EntityType == nameof(Vehicle) && a.EntityId == vehicleId.ToString()).ToListAsync(Ct);
        var outbox = await db.Set<OutboxMessage>()
            .Where(m => m.Type == typeof(VehicleAvailabilityChanged).FullName && m.Payload.Contains(vehicleId.ToString()))
            .CountAsync(Ct);

        audit.ShouldContain(a => a.Action == AuditActions.EntityCreated && a.UserName == "gerant@demo.local");
        audit.ShouldAllBe(a => a.After == null || !a.After.Contains("\"Revision\""));
        outbox.ShouldBe(4);
        (await db.Set<AuditLog>().AnyAsync(a => a.EntityType == nameof(VehicleDocument), Ct)).ShouldBeTrue();
        (await db.Set<AuditLog>().AnyAsync(a => a.EntityType == nameof(VehicleHistoryEntry), Ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task A_category_is_deleted_logically_and_frees_its_code()
    {
        var agencyId = await FleetData.NewAgencyAsync(host, Ct);
        var categoryId = await FleetData.NewCategoryAsync(host, agencyId, "SDAR", Ct);

        await using (var scope = host.CreateScope(FleetData.Manager))
        {
            (await scope.ServiceProvider.GetRequiredService<VehicleCategoryService>().DeleteAsync(categoryId, Ct)).IsSuccess.ShouldBeTrue();
        }

        var recreated = await FleetData.NewCategoryAsync(host, agencyId, "SDAR", Ct);
        await using var check = host.CreateScope();
        var db = check.ServiceProvider.GetRequiredService<CarRentalDbContext>();
        (await db.Set<VehicleCategory>().AnyAsync(c => c.Id == categoryId, Ct)).ShouldBeFalse();
        (await db.Set<VehicleCategory>().IgnoreQueryFilters().SingleAsync(c => c.Id == categoryId, Ct)).IsDeleted.ShouldBeTrue();
        recreated.ShouldNotBe(categoryId);
    }
}

internal static class VehicleFormExtensions
{
    public static VehicleDetails ToDetails(this VehicleForm form) => new(
        form.PlateNumber,
        form.Make,
        form.Model,
        form.Year,
        form.Color,
        form.Transmission,
        form.Fuel,
        form.Seats,
        form.LargeBags,
        form.SmallBags,
        form.InServiceOn,
        form.FirstRegistrationOn);
}
