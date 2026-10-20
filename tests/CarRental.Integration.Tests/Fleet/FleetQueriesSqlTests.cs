using CarRental.Application.Fleet;
using CarRental.Application.Fleet.Contracts;
using CarRental.Domain.Common;
using CarRental.Domain.Fleet;
using Microsoft.Extensions.DependencyInjection;

namespace CarRental.Integration.Tests.Fleet;

/// <summary>The IFleetQueries contract against SQL Server (docs/conception/phase-1-exploitation.md §3 and §5).</summary>
public sealed class FleetQueriesSqlTests(IntegrationHost host)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Categories_are_listed_by_display_order_then_code_and_found_by_code()
    {
        var agencyId = await FleetData.NewAgencyAsync(host, Ct);
        var suv = await FleetData.NewCategoryAsync(host, agencyId, "IFMR", Ct, displayOrder: 5);
        var compact = await FleetData.NewCategoryAsync(host, agencyId, "CDMR", Ct, displayOrder: 4);
        var economy = await FleetData.NewCategoryAsync(host, agencyId, "ECMR", Ct, displayOrder: 4);
        var mini = await FleetData.NewCategoryAsync(host, agencyId, "MCMR", Ct, displayOrder: 1);
        await using (var scope = host.CreateScope(FleetData.Manager))
        {
            (await scope.ServiceProvider.GetRequiredService<VehicleCategoryService>().SetActiveAsync(mini, false, Ct)).IsSuccess.ShouldBeTrue();
        }

        await using var query = host.CreateScope();
        var fleet = query.ServiceProvider.GetRequiredService<IFleetQueries>();

        (await fleet.GetCategoriesAsync(agencyId, cancellationToken: Ct)).Select(c => c.Id).ShouldBe([compact, economy, suv]);
        (await fleet.GetCategoriesAsync(agencyId, includeInactive: true, Ct)).Select(c => c.AcrissCode).ShouldBe(["MCMR", "CDMR", "ECMR", "IFMR"]);
        (await fleet.FindCategoryByCodeAsync(agencyId, "ecmr", Ct))!.Id.ShouldBe(economy);
        (await fleet.FindCategoryByCodeAsync(agencyId, "MCMR", Ct)).ShouldBeNull();
        var info = (await fleet.GetCategoryAsync(compact, Ct)).ShouldNotBeNull();
        info.Deposit.ShouldBe(Money.Tnd(600m));
        info.ReducedDeposit.ShouldBe(Money.Tnd(300m));
        info.Fuel.ShouldBe(FuelTypeInfo.Petrol);
        info.Transmission.ShouldBe(TransmissionInfo.Manual);
    }

    [Fact]
    public async Task Vehicles_expose_their_compliance_and_sold_ones_leave_the_category()
    {
        var agencyId = await FleetData.NewAgencyAsync(host, Ct);
        var categoryId = await FleetData.NewCategoryAsync(host, agencyId, "ECMR", Ct);
        var compliant = await FleetData.NewVehicleAsync(host, categoryId, Ct, documentsExpireOn: new DateOnly(2031, 1, 31), plate: "201 TU 1001");
        var missing = await FleetData.NewVehicleAsync(host, categoryId, Ct, withDocuments: false, plate: "202 TU 1002");
        var sold = await FleetData.NewVehicleAsync(host, categoryId, Ct, plate: "200 TU 1000");
        await using (var scope = host.CreateScope(FleetData.Manager))
        {
            (await scope.ServiceProvider.GetRequiredService<VehicleService>().SellAsync(sold, new SaleForm(DateTimeOffset.UtcNow.AddMinutes(-5), 30_000m, null), Ct)).IsSuccess.ShouldBeTrue();
        }

        await using var query = host.CreateScope();
        var fleet = query.ServiceProvider.GetRequiredService<IFleetQueries>();
        var vehicles = await fleet.GetVehiclesAsync(categoryId, Ct);

        vehicles.Select(v => v.Id).ShouldBe([compliant, missing]);
        vehicles[0].CompliantUntil.ShouldBe(new DateTimeOffset(2031, 1, 31, 23, 0, 0, TimeSpan.Zero));
        vehicles[1].CompliantUntil.ShouldBeNull();
        vehicles[1].IsCompliantFor(Period.Create(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1)).Value).ShouldBeFalse();
        (await fleet.GetVehicleAsync(sold, Ct))!.Status.ShouldBe(VehicleStatusInfo.Sold);
        var deadlines = await fleet.GetDeadlinesAsync(compliant, Ct);
        deadlines.Select(d => d.Type).ShouldBe([VehicleDocumentTypeInfo.Insurance, VehicleDocumentTypeInfo.RoadTax, VehicleDocumentTypeInfo.TechnicalInspection]);
        deadlines.ShouldAllBe(d => d.IsMandatory && d.ExpiresOn == new DateOnly(2031, 1, 31));
    }

    [Fact]
    public async Task Availability_windows_follow_documents_maintenance_status_and_sale()
    {
        var agencyId = await FleetData.NewAgencyAsync(host, Ct);
        var categoryId = await FleetData.NewCategoryAsync(host, agencyId, "EDAR", Ct);
        var free = await FleetData.NewVehicleAsync(host, categoryId, Ct, plate: "301 TU 1000");
        var maintained = await FleetData.NewVehicleAsync(host, categoryId, Ct, plate: "302 TU 1000");
        var expiring = await FleetData.NewVehicleAsync(host, categoryId, Ct, plate: "303 TU 1000", documentsExpireOn: TunisTime.Today(TimeProvider.System).AddDays(5));
        var outOfService = await FleetData.NewVehicleAsync(host, categoryId, Ct, plate: "304 TU 1000");
        var undocumented = await FleetData.NewVehicleAsync(host, categoryId, Ct, plate: "305 TU 1000", withDocuments: false);
        var sold = await FleetData.NewVehicleAsync(host, categoryId, Ct, plate: "300 TU 1000");

        var now = DateTimeOffset.UtcNow;
        var range = Period.Create(now.AddDays(1), now.AddDays(10)).Value;
        var block = Period.Create(now.AddDays(3), now.AddDays(4)).Value;
        await using (var scope = host.CreateScope(FleetData.Manager))
        {
            var maintenance = scope.ServiceProvider.GetRequiredService<VehicleMaintenanceService>();
            var vehicles = scope.ServiceProvider.GetRequiredService<VehicleService>();
            (await maintenance.ScheduleAsync(maintained, new MaintenanceForm(MaintenanceType.Tyres, block.Start, block.End, null, null, null), Ct)).IsSuccess.ShouldBeTrue();
            (await vehicles.ChangeStatusAsync(outOfService, VehicleStatus.OutOfService, "Sinistre", Ct)).IsSuccess.ShouldBeTrue();
            (await vehicles.SellAsync(sold, new SaleForm(now.AddMinutes(-1), 1m, null), Ct)).IsSuccess.ShouldBeTrue();
        }

        await using var query = host.CreateScope();
        var availability = await query.ServiceProvider.GetRequiredService<IFleetQueries>().GetAvailabilityAsync(categoryId, range, Ct);

        availability.Select(a => a.VehicleId).ShouldBe([free, maintained, expiring, outOfService, undocumented]);
        availability[0].Windows.ShouldBe([range]);
        availability[1].Windows.ShouldBe([Period.Create(range.Start, block.Start).Value, Period.Create(block.End, range.End).Value]);
        availability[2].Windows.ShouldHaveSingleItem().End.ShouldBe(VehicleCompliance.EndOfTunisDay(TunisTime.Today(TimeProvider.System).AddDays(5)));
        availability[2].Windows[0].Start.ShouldBe(range.Start);
        availability[3].Windows.ShouldBeEmpty();
        availability[4].Windows.ShouldBeEmpty();
        availability.ShouldAllBe(a => a.Windows.All(w => range.Contains(w)));
    }

    [Fact]
    public async Task Completing_a_maintenance_early_frees_the_rest_of_its_block()
    {
        var agencyId = await FleetData.NewAgencyAsync(host, Ct);
        var categoryId = await FleetData.NewCategoryAsync(host, agencyId, "CDMR", Ct);
        var vehicleId = await FleetData.NewVehicleAsync(host, categoryId, Ct);
        var now = DateTimeOffset.UtcNow;
        await using (var scope = host.CreateScope(FleetData.Actor(CarRental.Application.Identity.Contracts.Roles.Preparer)))
        {
            var maintenance = scope.ServiceProvider.GetRequiredService<VehicleMaintenanceService>();
            var scheduled = await maintenance.ScheduleAsync(vehicleId, new MaintenanceForm(MaintenanceType.Repair, now.AddMinutes(-30), now.AddDays(2), "Embrayage", null, null), Ct);
            (await maintenance.StartAsync(vehicleId, scheduled.Value, null, Ct)).IsSuccess.ShouldBeTrue();
            (await maintenance.CompleteAsync(vehicleId, scheduled.Value, new MaintenanceCompletionForm(null, 15_200, 880m, null), Ct)).IsSuccess.ShouldBeTrue();
        }

        await using var query = host.CreateScope();
        var range = Period.Create(now.AddHours(1), now.AddDays(3)).Value;
        var windows = (await query.ServiceProvider.GetRequiredService<IFleetQueries>().GetAvailabilityAsync(categoryId, range, Ct)).ShouldHaveSingleItem().Windows;
        var vehicle = (await query.ServiceProvider.GetRequiredService<IFleetQueries>().GetVehicleAsync(vehicleId, Ct))!;

        windows.ShouldBe([range]);
        vehicle.Status.ShouldBe(VehicleStatusInfo.Active);
        vehicle.Mileage.ShouldBe(15_200);
    }
}
