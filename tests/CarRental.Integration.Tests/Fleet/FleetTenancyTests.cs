using CarRental.Application.Common.Persistence;
using CarRental.Application.Fleet;
using CarRental.Application.Fleet.Contracts;
using CarRental.Application.Fleet.Photos;
using CarRental.Application.Identity.Contracts;
using CarRental.Domain.Common;
using CarRental.Domain.Fleet;
using CarRental.Infrastructure.Persistence.Interceptors;
using CarRental.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CarRental.Integration.Tests.Fleet;

/// <summary>
/// The fleets of two agencies never meet (ADR-0067), on SQL Server: the staff of an agency list, read and change their own
/// categories and vehicles only, and each agency may reuse the category codes and the plates of another.
/// </summary>
public sealed class FleetTenancyTests(IntegrationHost host)
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_staff_of_an_agency_list_and_read_their_own_fleet_only()
    {
        var (a, b) = await TwoFleetsAsync();

        await using var scope = host.CreateScope(TestActors.Staff(a.AgencyId, Roles.Manager));
        var admin = scope.ServiceProvider.GetRequiredService<FleetAdminQueries>();
        (await admin.ListCategoriesAsync(cancellationToken: Ct)).Value.Select(c => c.Id).ShouldBe([a.CategoryId]);
        (await admin.ListVehiclesAsync(new VehicleListFilter { IncludeSold = true }, Ct)).Value.Select(v => v.Id).ShouldBe([a.VehicleId]);
        (await admin.ListVehiclesAsync(new VehicleListFilter { CategoryId = b.CategoryId }, Ct)).Value.ShouldBeEmpty();
        (await admin.GetVehicleAsync(b.VehicleId, Ct)).Error.ShouldBe(FleetErrors.VehicleNotFound);
        (await admin.GetVehicleAsync(a.VehicleId, Ct)).Value.CategoryCode.ShouldBe("ECMR");
        (await admin.GetDeadlinesAsync(366, Ct)).Value.ShouldAllBe(d => d.VehicleId == a.VehicleId);
        (await admin.GetDashboardAsync(Ct)).Value.VehicleCount.ShouldBe(1);

        var fleet = scope.ServiceProvider.GetRequiredService<IFleetQueries>();
        (await fleet.GetCategoriesAsync(includeInactive: true, cancellationToken: Ct)).Select(c => c.Id).ShouldBe([a.CategoryId]);
        (await fleet.GetCategoriesAsync(b.AgencyId, includeInactive: true, Ct)).ShouldBeEmpty();
        (await fleet.GetCategoryAsync(b.CategoryId, Ct)).ShouldBeNull();
        (await fleet.FindCategoryByCodeAsync(b.AgencyId, "ECMR", Ct)).ShouldBeNull();
        (await fleet.GetVehicleAsync(b.VehicleId, Ct)).ShouldBeNull();
        (await fleet.GetVehiclesAsync(b.CategoryId, Ct)).ShouldBeEmpty();
        (await fleet.GetAvailabilityAsync(b.CategoryId, Period.FromDuration(DateTimeOffset.UtcNow, TimeSpan.FromDays(7)).Value, Ct)).ShouldBeEmpty();
        (await fleet.GetDeadlinesAsync(b.VehicleId, Ct)).ShouldBeEmpty();
        (await fleet.GetVehicleAsync(a.VehicleId, Ct))!.PlateNumber.ShouldBe(a.Plate);

        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        (await db.Set<VehicleDocument>().AnyAsync(d => d.VehicleId == b.VehicleId, Ct)).ShouldBeFalse("the documents of another agency's vehicle are hidden too");
        (await db.Set<VehicleHistoryEntry>().AnyAsync(h => h.VehicleId == b.VehicleId, Ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task The_staff_of_an_agency_never_change_the_fleet_of_another()
    {
        var (a, b) = await TwoFleetsAsync();

        await using (var scope = host.CreateScope(TestActors.Staff(a.AgencyId, Roles.Manager)))
        {
            var vehicles = scope.ServiceProvider.GetRequiredService<VehicleService>();
            var categories = scope.ServiceProvider.GetRequiredService<VehicleCategoryService>();
            var maintenance = scope.ServiceProvider.GetRequiredService<VehicleMaintenanceService>();
            var photos = scope.ServiceProvider.GetRequiredService<FleetPhotoService>();

            (await vehicles.UpdateAsync(b.VehicleId, FleetData.Form(b.CategoryId, b.Plate), Ct)).Error.ShouldBe(FleetErrors.VehicleNotFound);
            (await vehicles.RecordMileageAsync(b.VehicleId, 99_000, Ct)).Error.ShouldBe(FleetErrors.VehicleNotFound);
            (await vehicles.ChangeStatusAsync(b.VehicleId, VehicleStatus.OutOfService, "Intrusion", Ct)).Error.ShouldBe(FleetErrors.VehicleNotFound);
            (await vehicles.ChangeCategoryAsync(a.VehicleId, b.CategoryId, Ct)).Error.ShouldBe(FleetErrors.CategoryNotFound);
            (await vehicles.RegisterAsync(FleetData.Form(b.CategoryId), 0, Ct)).Error.ShouldBe(FleetErrors.CategoryNotFound);
            (await maintenance.ScheduleAsync(
                b.VehicleId,
                new MaintenanceForm(MaintenanceType.OilChange, DateTimeOffset.UtcNow.AddDays(3), DateTimeOffset.UtcNow.AddDays(4), null, null, null),
                Ct)).Error.ShouldBe(FleetErrors.VehicleNotFound);
            (await categories.UpdateAsync(b.CategoryId, CategoryForm("ECMR"), Ct)).Error.ShouldBe(FleetErrors.CategoryNotFound);
            (await categories.SetActiveAsync(b.CategoryId, false, Ct)).Error.ShouldBe(FleetErrors.CategoryNotFound);
            (await categories.DeleteAsync(b.CategoryId, Ct)).Error.ShouldBe(FleetErrors.CategoryNotFound);
            (await categories.CreateAsync(CategoryForm("CDMR") with { AgencyId = b.AgencyId }, Ct)).Error.ShouldBe(FleetErrors.Forbidden);
            (await photos.UploadVehiclePhotoAsync(b.VehicleId, new MemoryStream(Png), Ct)).Error.ShouldBe(FleetErrors.VehicleNotFound);
            (await photos.UploadCategoryPhotoAsync(b.CategoryId, new MemoryStream(Png), Ct)).Error.ShouldBe(FleetErrors.CategoryNotFound);
            (await photos.FindVehiclePhotoAsync(b.VehicleId, Guid.CreateVersion7(), Ct)).Error.ShouldBe(FleetErrors.PhotoNotFound);

            // Even a write made without the services is refused by the persistence layer.
            var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
            db.Set<Vehicle>().Add(Vehicle.Register(b.AgencyId, b.CategoryId, Details(FleetData.NewPlate()), 0, new ChangeStamp(DateTimeOffset.UtcNow, "intrus")).Value);
            await Should.ThrowAsync<AgencyBoundaryException>(() => db.SaveChangesAsync(Ct));
        }

        await using var check = host.CreateScope();
        var vehicle = await check.ServiceProvider.GetRequiredService<IAppDbContext>().Set<Vehicle>().AsNoTracking().SingleAsync(v => v.Id == b.VehicleId, Ct);
        (vehicle.Mileage, vehicle.Status, vehicle.CategoryId).ShouldBe((15_000, VehicleStatus.Active, b.CategoryId));
        (await check.ServiceProvider.GetRequiredService<IAppDbContext>().Set<Vehicle>().CountAsync(v => v.AgencyId == b.AgencyId, Ct)).ShouldBe(1);
    }

    [Fact]
    public async Task Category_codes_and_plates_are_unique_within_an_agency_only()
    {
        // Both agencies already hold an ECMR category and a vehicle with the same plate.
        var (a, b) = await TwoFleetsAsync();

        await using (var scope = host.CreateScope(TestActors.Staff(a.AgencyId, Roles.Manager)))
        {
            (await scope.ServiceProvider.GetRequiredService<VehicleService>().RegisterAsync(FleetData.Form(a.CategoryId, a.Plate), 0, Ct))
                .Error.ShouldBe(FleetErrors.PlateNumberTaken);
            (await scope.ServiceProvider.GetRequiredService<VehicleCategoryService>().CreateAsync(CategoryForm("ECMR"), Ct))
                .Error.ShouldBe(FleetErrors.CategoryCodeTaken);
        }

        await using (var scope = host.CreateScope(TestActors.Staff(b.AgencyId, Roles.Manager)))
        {
            var created = await scope.ServiceProvider.GetRequiredService<VehicleCategoryService>().CreateAsync(CategoryForm("CDMR"), Ct);
            created.IsSuccess.ShouldBeTrue(created.IsFailure ? created.Error.ToString() : null);
            (await scope.ServiceProvider.GetRequiredService<VehicleService>().RegisterAsync(FleetData.Form(created.Value, a.Plate), 0, Ct))
                .Error.ShouldBe(FleetErrors.PlateNumberTaken, "the plate is taken in this agency too, by its own vehicle");
        }

        await using var check = host.CreateScope();
        var db = check.ServiceProvider.GetRequiredService<IAppDbContext>();
        var (ecmr, cdmr) = (AcrissCode.Create("ECMR").Value, AcrissCode.Create("CDMR").Value);
        (await db.Set<Vehicle>().Where(v => v.PlateNumber == a.Plate).Select(v => v.AgencyId).ToListAsync(Ct)).ShouldBe([a.AgencyId, b.AgencyId], ignoreOrder: true);
        (await db.Set<VehicleCategory>().CountAsync(c => (c.AgencyId == a.AgencyId || c.AgencyId == b.AgencyId) && c.Code == ecmr, Ct)).ShouldBe(2);
        (await db.Set<VehicleCategory>().CountAsync(c => (c.AgencyId == a.AgencyId || c.AgencyId == b.AgencyId) && c.Code == cdmr, Ct)).ShouldBe(1);
    }

    [Fact]
    public async Task The_children_of_a_vehicle_carry_its_agency()
    {
        var (a, _) = await TwoFleetsAsync();
        await using (var scope = host.CreateScope(TestActors.Staff(a.AgencyId, Roles.Manager)))
        {
            var maintenance = scope.ServiceProvider.GetRequiredService<VehicleMaintenanceService>();
            (await maintenance.SetPlanAsync(a.VehicleId, new MaintenancePlanForm(MaintenanceType.OilChange, 10_000, 12, null, null, "Vidange"), Ct)).IsSuccess.ShouldBeTrue();
            (await maintenance.ScheduleAsync(
                a.VehicleId,
                new MaintenanceForm(MaintenanceType.Brakes, DateTimeOffset.UtcNow.AddDays(3), DateTimeOffset.UtcNow.AddDays(4), "Plaquettes", null, 200m),
                Ct)).IsSuccess.ShouldBeTrue();
            var vehicles = scope.ServiceProvider.GetRequiredService<VehicleService>();
            (await vehicles.AddCostAsync(a.VehicleId, new CostForm(new DateOnly(2026, 1, 10), "Traceur GPS", 300m), Ct)).IsSuccess.ShouldBeTrue();
            (await vehicles.SetEconomicsAsync(a.VehicleId, new EconomicsForm(50_000m, new DateOnly(2025, 1, 15), FinancingType.Cash, null, null, 5, 15_000m), Ct)).IsSuccess.ShouldBeTrue();
            (await scope.ServiceProvider.GetRequiredService<FleetPhotoService>().UploadVehiclePhotoAsync(a.VehicleId, new MemoryStream(Png), Ct)).IsSuccess.ShouldBeTrue();
        }

        await using var check = host.CreateScope();
        var db = check.ServiceProvider.GetRequiredService<IAppDbContext>();
        IEnumerable<Guid> agencies =
        [
            .. await db.Set<VehicleDocument>().Where(d => d.VehicleId == a.VehicleId).Select(d => d.AgencyId).ToListAsync(Ct),
            .. await db.Set<MaintenancePlan>().Where(p => p.VehicleId == a.VehicleId).Select(p => p.AgencyId).ToListAsync(Ct),
            .. await db.Set<MaintenanceEvent>().Where(e => e.VehicleId == a.VehicleId).Select(e => e.AgencyId).ToListAsync(Ct),
            .. await db.Set<VehicleCost>().Where(c => c.VehicleId == a.VehicleId).Select(c => c.AgencyId).ToListAsync(Ct),
            .. await db.Set<VehicleEconomics>().Where(e => e.VehicleId == a.VehicleId).Select(e => e.AgencyId).ToListAsync(Ct),
            .. await db.Set<VehiclePhoto>().Where(p => p.VehicleId == a.VehicleId).Select(p => p.AgencyId).ToListAsync(Ct),
            .. await db.Set<VehicleHistoryEntry>().Where(h => h.VehicleId == a.VehicleId).Select(h => h.AgencyId).ToListAsync(Ct),
        ];
        agencies.Count().ShouldBeGreaterThan(8);
        agencies.ShouldAllBe(agencyId => agencyId == a.AgencyId);
    }

    /// <summary>Two agencies, each with an ECMR category and one vehicle, both vehicles with the same plate.</summary>
    private async Task<(AgencyFleet A, AgencyFleet B)> TwoFleetsAsync()
    {
        var plate = FleetData.NewPlate();
        var fleets = new List<AgencyFleet>();
        for (var i = 0; i < 2; i++)
        {
            var agencyId = await FleetData.NewAgencyAsync(host, Ct);
            var categoryId = await FleetData.NewCategoryAsync(host, agencyId, "ECMR", Ct);
            var vehicleId = await FleetData.NewVehicleAsync(host, categoryId, Ct, plate: plate);
            fleets.Add(new AgencyFleet(agencyId, categoryId, vehicleId, plate));
        }

        return (fleets[0], fleets[1]);
    }

    private static CategoryForm CategoryForm(string code) =>
        new(code, $"Catégorie {code}", "Peugeot 208", 5, 5, 1, 2, VehicleCategory.FuelOf(AcrissCode.Create(code).Value) ?? FuelType.Petrol, 600m, 300m, 1);

    private static VehicleDetails Details(string plate) =>
        new(plate, "Renault", "Clio", 2025, null, Transmission.Manual, FuelType.Petrol, 5, 1, 2, new DateOnly(2025, 1, 15));

    private sealed record AgencyFleet(Guid AgencyId, Guid CategoryId, Guid VehicleId, string Plate);
}
