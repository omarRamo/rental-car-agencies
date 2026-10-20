using CarRental.Application.Common.Paging;
using CarRental.Application.Common.Security;
using CarRental.Application.Fleet;
using CarRental.Application.Identity.Contracts;
using CarRental.Application.Identity.Users;
using CarRental.Application.Referential.Contracts;
using CarRental.Application.Tests.Fakes;
using CarRental.Application.Tests.Fleet.Fakes;
using CarRental.Domain.Common;
using CarRental.Domain.Fleet;
using Microsoft.Extensions.DependencyInjection;

namespace CarRental.Application.Tests.Fleet;

public sealed class FleetAdminQueriesTests
{
    private static readonly DateOnly Today = new(2026, 10, 20);
    private readonly FleetTestBed _bed = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Fleet_screens_need_the_read_permission()
    {
        var queries = new FleetAdminQueries(_bed.Db, new ScopedUserContext([]), new DefaultSettingsReader(), _bed.Time);

        (await queries.ListVehiclesAsync(new VehicleListFilter(), Ct)).Error.ShouldBe(FleetErrors.Forbidden);
        (await queries.ListCategoriesAsync(cancellationToken: Ct)).Error.ShouldBe(FleetErrors.Forbidden);
        (await queries.GetVehicleAsync(Guid.CreateVersion7(), Ct)).Error.ShouldBe(FleetErrors.Forbidden);
        (await queries.GetDeadlinesAsync(cancellationToken: Ct)).Error.ShouldBe(FleetErrors.Forbidden);
        (await queries.GetDashboardAsync(Ct)).Error.ShouldBe(FleetErrors.Forbidden);
    }

    [Theory]
    [InlineData(Roles.Manager)]
    [InlineData(Roles.Agent)]
    [InlineData(Roles.Accountant)]
    [InlineData(Roles.Preparer)]
    public async Task Every_role_reads_the_fleet(string role)
    {
        _bed.AddVehicle(_bed.AddCategory());

        (await _bed.AdminQueries(role).ListVehiclesAsync(new VehicleListFilter(), Ct)).Value.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task The_vehicle_list_filters_by_category_status_deadline_and_text()
    {
        var economy = _bed.AddCategory("ECMR");
        var compact = _bed.AddCategory("CDMR", FuelType.Diesel);
        var ok = _bed.AddVehicle(economy, "210 TU 1000");
        var soon = _bed.AddVehicle(economy, "211 TU 1000", documentsExpireOn: Today.AddDays(20));
        var critical = _bed.AddVehicle(compact, "212 TU 1000", documentsExpireOn: Today.AddDays(3));
        var maintenance = _bed.AddVehicle(compact, "213 TU 1000");
        maintenance.ChangeStatus(VehicleStatus.Maintenance, "Révision", FleetTestBed.Stamp()).IsSuccess.ShouldBeTrue();
        var sold = _bed.AddVehicle(compact, "214 TU 1000");
        sold.Sell(FleetTestBed.Now, Money.Tnd(1m), null, FleetTestBed.Stamp()).IsSuccess.ShouldBeTrue();
        var queries = _bed.AdminQueries();

        async Task<IReadOnlyList<Guid>> Ids(VehicleListFilter filter) => [.. (await queries.ListVehiclesAsync(filter, Ct)).Value.Select(v => v.Id)];

        (await Ids(new VehicleListFilter())).ShouldBe([ok.Id, soon.Id, critical.Id, maintenance.Id]);
        (await Ids(new VehicleListFilter { IncludeSold = true })).ShouldBe([ok.Id, soon.Id, critical.Id, maintenance.Id, sold.Id]);
        (await Ids(new VehicleListFilter { Status = VehicleStatus.Sold })).ShouldBe([sold.Id]);
        (await Ids(new VehicleListFilter { CategoryId = economy.Id })).ShouldBe([ok.Id, soon.Id]);
        (await Ids(new VehicleListFilter { Status = VehicleStatus.Maintenance })).ShouldBe([maintenance.Id]);
        (await Ids(new VehicleListFilter { Deadline = DeadlineState.DueSoon })).ShouldBe([soon.Id]);
        (await Ids(new VehicleListFilter { Deadline = DeadlineState.Critical })).ShouldBe([critical.Id]);
        (await Ids(new VehicleListFilter { Search = " 212 tu " })).ShouldBe([critical.Id]);
        (await Ids(new VehicleListFilter { Search = "peugeot" })).Count.ShouldBe(4);

        var item = (await queries.ListVehiclesAsync(new VehicleListFilter { Search = "211" }, Ct)).Value.ShouldHaveSingleItem();
        item.CategoryCode.ShouldBe("ECMR");
        item.Deadline.State.ShouldBe(DeadlineState.DueSoon);
        item.Deadline.DaysLeft.ShouldBe(20);
    }

    [Fact]
    public async Task A_missing_mandatory_document_is_the_worst_deadline()
    {
        var category = _bed.AddCategory();
        var vehicle = Vehicle.Register(_bed.AgencyId, category.Id, FleetTestBed.Form(category.Id, "299 TU 1").ToDetailsForTests(), 0, FleetTestBed.Stamp()).Value;
        vehicle.RecordDocument(new VehicleDocumentDetails(VehicleDocumentType.Insurance, "POL", null, Today.AddDays(2)), FleetTestBed.Stamp()).IsSuccess.ShouldBeTrue();
        _bed.Db.Fake<Vehicle>().Add(vehicle);

        var item = (await _bed.AdminQueries().ListVehiclesAsync(new VehicleListFilter(), Ct)).Value.ShouldHaveSingleItem();

        item.Deadline.State.ShouldBe(DeadlineState.Missing);
        item.Deadline.Type.ShouldBe(VehicleDocumentType.RoadTax);
    }

    [Fact]
    public async Task The_vehicle_detail_gathers_every_tab()
    {
        var category = _bed.AddCategory();
        var vehicle = _bed.AddVehicle(category, mileage: 49_500);
        vehicle.SetMaintenancePlan(new MaintenancePlanDetails(MaintenanceType.OilChange, 10_000, 12, Today.AddMonths(-6), 40_000), FleetTestBed.Stamp()).IsSuccess.ShouldBeTrue();
        vehicle.ScheduleMaintenance(new MaintenanceDetails(MaintenanceType.OilChange, FleetTestBed.Now.AddDays(2), FleetTestBed.Now.AddDays(2).AddHours(3)), FleetTestBed.Stamp()).IsSuccess.ShouldBeTrue();
        vehicle.SetEconomics(new VehicleEconomicsDetails(Money.Tnd(60_000m), new DateOnly(2024, 1, 1), FinancingType.Cash, null, null, 5, Money.Tnd(18_000m)), FleetTestBed.Stamp()).IsSuccess.ShouldBeTrue();
        vehicle.AddCost(Today, "Traceur", Money.Tnd(300m), FleetTestBed.Stamp()).IsSuccess.ShouldBeTrue();

        var detail = (await _bed.AdminQueries().GetVehicleAsync(vehicle.Id, Ct)).Value;

        detail.PlateNumber.ShouldBe("215 TU 4512");
        detail.CategoryCode.ShouldBe("ECMR");
        detail.Documents.Count.ShouldBe(3);
        detail.Deadline.State.ShouldBe(DeadlineState.Ok);
        detail.MaintenancePlans.ShouldHaveSingleItem().Due.State.ShouldBe(MaintenanceDueState.DueSoon);
        detail.Maintenances.ShouldHaveSingleItem().Status.ShouldBe(MaintenanceStatus.Planned);
        detail.AvailabilityNext30Days.Count.ShouldBe(2);
        detail.Economics.ShouldNotBeNull().Schedule.Count.ShouldBe(12);
        detail.Economics.BookValue.ShouldBe(Money.Tnd(60_000m) - detail.Economics.AccumulatedDepreciation);
        detail.Costs.ShouldHaveSingleItem().Amount.ShouldBe(Money.Tnd(300m));
        (await _bed.AdminQueries().GetVehicleAsync(Guid.CreateVersion7(), Ct)).Error.ShouldBe(FleetErrors.VehicleNotFound);
    }

    [Fact]
    public async Task Deadlines_show_missing_expired_and_upcoming_documents_most_urgent_first()
    {
        var category = _bed.AddCategory();
        _bed.AddVehicle(category, "210 TU 1", documentsExpireOn: Today.AddDays(200));
        var soon = _bed.AddVehicle(category, "211 TU 1", documentsExpireOn: Today.AddDays(200));
        soon.RecordDocument(new VehicleDocumentDetails(VehicleDocumentType.RoadTax, "VIG", null, Today.AddDays(20)), FleetTestBed.Stamp()).IsSuccess.ShouldBeTrue();
        var expired = _bed.AddVehicle(category, "212 TU 1", documentsExpireOn: Today.AddDays(200));
        expired.RecordDocument(new VehicleDocumentDetails(VehicleDocumentType.TechnicalInspection, "VT", null, Today.AddDays(-3)), FleetTestBed.Stamp()).IsSuccess.ShouldBeTrue();
        var missing = Vehicle.Register(_bed.AgencyId, category.Id, FleetTestBed.Form(category.Id, "213 TU 1").ToDetailsForTests(), 0, FleetTestBed.Stamp()).Value;
        _bed.Db.Fake<Vehicle>().Add(missing);

        var deadlines = (await _bed.AdminQueries().GetDeadlinesAsync(60, Ct)).Value;

        deadlines.Select(d => (d.PlateNumber, d.Type, d.State)).ShouldBe(
        [
            ("213 TU 1", VehicleDocumentType.Insurance, DeadlineState.Missing),
            ("213 TU 1", VehicleDocumentType.RoadTax, DeadlineState.Missing),
            ("213 TU 1", VehicleDocumentType.TechnicalInspection, DeadlineState.Missing),
            ("212 TU 1", VehicleDocumentType.TechnicalInspection, DeadlineState.Expired),
            ("211 TU 1", VehicleDocumentType.RoadTax, DeadlineState.DueSoon),
        ]);
        deadlines[3].DaysLeft.ShouldBe(-3);
    }

    [Fact]
    public async Task The_dashboard_counts_alerts_and_lists_unavailable_vehicles()
    {
        var category = _bed.AddCategory();
        _bed.AddVehicle(category, "210 TU 1");
        var soon = _bed.AddVehicle(category, "211 TU 1", documentsExpireOn: Today.AddDays(5));
        var outOfService = _bed.AddVehicle(category, "212 TU 1");
        outOfService.ChangeStatus(VehicleStatus.OutOfService, "Accident", FleetTestBed.Stamp()).IsSuccess.ShouldBeTrue();

        var dashboard = (await _bed.AdminQueries(Roles.Agent).GetDashboardAsync(Ct)).Value;

        dashboard.AlertCount.ShouldBe(3);
        dashboard.Alerts.ShouldAllBe(a => a.VehicleId == soon.Id && a.State == DeadlineState.Critical);
        var unavailable = dashboard.Unavailable.ShouldHaveSingleItem();
        unavailable.VehicleId.ShouldBe(outOfService.Id);
        unavailable.Reason.ShouldBe("Accident");
        dashboard.VehicleCount.ShouldBe(3);
        dashboard.AvailableNowCount.ShouldBe(2);
    }

    [Fact]
    public async Task Categories_are_listed_with_their_vehicle_count()
    {
        var used = _bed.AddCategory("ECMR");
        _bed.AddCategory("CDMR", FuelType.Diesel, displayOrder: 1, active: false);
        _bed.AddVehicle(used, "210 TU 1");
        _bed.AddVehicle(used, "211 TU 1");

        var categories = (await _bed.AdminQueries(Roles.Accountant).ListCategoriesAsync(cancellationToken: Ct)).Value;

        categories.Select(c => (c.Code, c.VehicleCount, c.IsActive)).ShouldBe([("CDMR", 0, false), ("ECMR", 2, true)]);
        (await _bed.AdminQueries().ListCategoriesAsync(includeInactive: false, Ct)).Value.ShouldHaveSingleItem().Code.ShouldBe("ECMR");
    }

    [Fact]
    public async Task Managers_receiving_alerts_are_the_active_users_of_the_role_with_an_email()
    {
        var directory = new FakeUserDirectory();
        directory.Add("gerant@demo.local", Roles.Manager);
        directory.Add("locked@demo.local", Roles.Manager, locked: true);
        directory.Add("agent@demo.local", Roles.Agent);
        var queries = new IdentityQueries(directory);

        var managers = await queries.GetActiveUsersInRoleAsync(Roles.Manager, Ct);

        managers.ShouldHaveSingleItem().Email.ShouldBe("gerant@demo.local");
        (await queries.GetActiveUsersInRoleAsync(Roles.Preparer, Ct)).ShouldBeEmpty();
        await Should.ThrowAsync<ArgumentException>(() => queries.GetActiveUsersInRoleAsync(" ", Ct));
    }

    [Fact]
    public async Task Managers_are_read_across_every_page_of_the_directory()
    {
        var directory = new FakeUserDirectory();
        for (var i = 0; i < PageRequest.MaxPageSize + 5; i++)
        {
            directory.Add($"gerant{i:D3}@demo.local", Roles.Manager);
        }

        (await new IdentityQueries(directory).GetActiveUsersInRoleAsync(Roles.Manager, Ct)).Count.ShouldBe(PageRequest.MaxPageSize + 5);
    }

    [Fact]
    public void The_module_declares_its_settings_job_seeder_and_services()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddApplication();

        var catalog = services.BuildServiceProvider().GetRequiredService<ISettingDefinitionCatalog>();

        catalog.Find("fleet.maintenance_due_soon_km").ShouldNotBeNull().DefaultValue.ShouldBe("1000");
        catalog.Find("fleet.maintenance_due_soon_days").ShouldNotBeNull().DefaultValue.ShouldBe("30");
        services.ShouldContain(d => d.ServiceType == typeof(CarRental.Application.Common.BackgroundJobs.RecurringJobDescriptor)
            && ((CarRental.Application.Common.BackgroundJobs.RecurringJobDescriptor)d.ImplementationInstance!).JobId == "fleet.deadline_alerts");
        services.ShouldContain(d => d.ServiceType == typeof(CarRental.Application.Common.Seeding.IDataSeeder)
            && d.ImplementationType == typeof(CarRental.Application.Fleet.Seeding.FleetDemoSeeder));
        services.ShouldContain(d => d.ServiceType == typeof(CarRental.Application.Fleet.Contracts.IFleetQueries));
    }
}
