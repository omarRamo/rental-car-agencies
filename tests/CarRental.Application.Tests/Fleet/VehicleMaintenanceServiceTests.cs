using CarRental.Application.Fleet;
using CarRental.Application.Identity.Contracts;
using CarRental.Domain.Common;
using CarRental.Domain.Fleet;

namespace CarRental.Application.Tests.Fleet;

public sealed class VehicleMaintenanceServiceTests
{
    private static readonly DateTimeOffset NextWeek = FleetTestBed.Now.AddDays(7);
    private readonly FleetTestBed _bed = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static MaintenanceForm Form(DateTimeOffset start, DateTimeOffset end) =>
        new(MaintenanceType.OilChange, start, end, "Vidange", "Garage", 150m);

    [Theory]
    [InlineData(Roles.Agent)]
    [InlineData(Roles.Accountant)]
    public async Task Agents_and_accountants_cannot_manage_maintenance(string role)
    {
        var vehicle = _bed.AddVehicle(_bed.AddCategory());
        var service = _bed.Maintenance(role);

        (await service.ScheduleAsync(vehicle.Id, Form(NextWeek, NextWeek.AddHours(4)), Ct)).Error.ShouldBe(FleetErrors.Forbidden);
        (await service.SetPlanAsync(vehicle.Id, new MaintenancePlanForm(MaintenanceType.OilChange, 10_000, 12, null, null, null), Ct)).Error.ShouldBe(FleetErrors.Forbidden);
        (await service.StartAsync(vehicle.Id, Guid.CreateVersion7(), null, Ct)).Error.ShouldBe(FleetErrors.Forbidden);
        (await service.CompleteAsync(vehicle.Id, Guid.CreateVersion7(), new MaintenanceCompletionForm(null, null, null, null), Ct)).Error.ShouldBe(FleetErrors.Forbidden);
        (await service.CancelAsync(vehicle.Id, Guid.CreateVersion7(), "x", Ct)).Error.ShouldBe(FleetErrors.Forbidden);
        (await service.RescheduleAsync(vehicle.Id, Guid.CreateVersion7(), NextWeek, NextWeek.AddHours(1), Ct)).Error.ShouldBe(FleetErrors.Forbidden);
        (await service.RemovePlanAsync(vehicle.Id, Guid.CreateVersion7(), Ct)).Error.ShouldBe(FleetErrors.Forbidden);
        vehicle.MaintenanceEvents.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_preparer_schedules_starts_and_completes_a_maintenance_and_every_step_is_signalled()
    {
        var category = _bed.AddCategory();
        var vehicle = _bed.AddVehicle(category);
        var service = _bed.Maintenance(Roles.Preparer);

        var scheduled = await service.ScheduleAsync(vehicle.Id, Form(FleetTestBed.Now.AddHours(1), FleetTestBed.Now.AddHours(6)), Ct);
        scheduled.IsSuccess.ShouldBeTrue(scheduled.IsFailure ? scheduled.Error.ToString() : null);
        (await service.StartAsync(vehicle.Id, scheduled.Value, null, Ct)).IsSuccess.ShouldBeTrue();
        vehicle.Status.ShouldBe(VehicleStatus.Maintenance);
        _bed.Time.Advance(TimeSpan.FromHours(2));
        (await service.CompleteAsync(vehicle.Id, scheduled.Value, new MaintenanceCompletionForm(null, 20_400, 175.250m, "OK"), Ct)).IsSuccess.ShouldBeTrue();

        var maintenance = vehicle.MaintenanceEvents.ShouldHaveSingleItem();
        maintenance.Status.ShouldBe(MaintenanceStatus.Completed);
        maintenance.Cost.ShouldBe(Money.Tnd(175.250m));
        vehicle.Status.ShouldBe(VehicleStatus.Active);
        vehicle.Mileage.ShouldBe(20_400);
        _bed.Notifier.Changed.ShouldBe([category.Id, category.Id, category.Id]);
        _bed.Lock.Acquired.ShouldBe([category.Id, category.Id, category.Id]);
    }

    [Fact]
    public async Task Rescheduling_and_cancelling_are_signalled()
    {
        var category = _bed.AddCategory();
        var vehicle = _bed.AddVehicle(category);
        var service = _bed.Maintenance();
        var scheduled = (await service.ScheduleAsync(vehicle.Id, Form(NextWeek, NextWeek.AddHours(4)), Ct)).Value;

        (await service.RescheduleAsync(vehicle.Id, scheduled, NextWeek.AddDays(1), NextWeek.AddDays(1).AddHours(4), Ct)).IsSuccess.ShouldBeTrue();
        (await service.CancelAsync(vehicle.Id, scheduled, " ", Ct)).Error.ShouldBe(FleetErrors.ReasonRequired);
        (await service.CancelAsync(vehicle.Id, scheduled, "Garage fermé", Ct)).IsSuccess.ShouldBeTrue();

        vehicle.MaintenanceEvents.ShouldHaveSingleItem().Status.ShouldBe(MaintenanceStatus.Cancelled);
        _bed.Notifier.Changed.Count.ShouldBe(3);
    }

    [Fact]
    public async Task Overlapping_and_invalid_maintenance_is_refused_without_signal()
    {
        var vehicle = _bed.AddVehicle(_bed.AddCategory());
        var service = _bed.Maintenance();
        (await service.ScheduleAsync(vehicle.Id, Form(NextWeek, NextWeek.AddHours(4)), Ct)).IsSuccess.ShouldBeTrue();
        _bed.Notifier.Changed.Clear();

        (await service.ScheduleAsync(vehicle.Id, Form(NextWeek.AddHours(2), NextWeek.AddHours(6)), Ct)).Error.ShouldBe(FleetErrors.MaintenanceOverlap);
        (await service.ScheduleAsync(vehicle.Id, Form(NextWeek, NextWeek.AddHours(-1)), Ct)).Error.ShouldBe(FleetErrors.MaintenanceBlockInvalid);
        (await service.ScheduleAsync(vehicle.Id, Form(NextWeek.AddDays(2), NextWeek.AddDays(3)) with { EstimatedCost = 1.2345m }, Ct)).Error.ShouldBe(FleetErrors.AmountInvalid);
        (await service.ScheduleAsync(Guid.CreateVersion7(), Form(NextWeek.AddDays(2), NextWeek.AddDays(3)), Ct)).Error.ShouldBe(FleetErrors.VehicleNotFound);

        _bed.Notifier.Changed.ShouldBeEmpty();
    }

    [Fact]
    public async Task Maintenance_plans_are_managed_without_signal()
    {
        var vehicle = _bed.AddVehicle(_bed.AddCategory());
        var service = _bed.Maintenance(Roles.Preparer);

        var plan = await service.SetPlanAsync(vehicle.Id, new MaintenancePlanForm(MaintenanceType.Tyres, 40_000, null, null, 15_000, "Pneus"), Ct);
        (await service.SetPlanAsync(vehicle.Id, new MaintenancePlanForm(MaintenanceType.Tyres, null, null, null, null, null), Ct)).Error.ShouldBe(FleetErrors.MaintenancePlanInvalid);

        plan.IsSuccess.ShouldBeTrue();
        vehicle.MaintenancePlans.ShouldHaveSingleItem().NextDueMileage.ShouldBe(55_000);
        (await service.RemovePlanAsync(vehicle.Id, plan.Value, Ct)).IsSuccess.ShouldBeTrue();
        vehicle.MaintenancePlans.ShouldBeEmpty();
        _bed.Notifier.Changed.ShouldBeEmpty();
    }
}
