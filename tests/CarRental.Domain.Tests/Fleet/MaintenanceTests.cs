using CarRental.Domain.Common;
using CarRental.Domain.Fleet;
using static CarRental.Domain.Tests.Fleet.FleetTestData;

namespace CarRental.Domain.Tests.Fleet;

public sealed class MaintenanceTests
{
    private static readonly DateTimeOffset NextWeek = Tunis(2026, 10, 27, 8);

    [Fact]
    public void A_scheduled_maintenance_blocks_its_dates_and_raises_the_capacity_event()
    {
        var vehicle = NewVehicle();

        var maintenance = vehicle.ScheduleMaintenance(Maintenance(NextWeek, NextWeek.AddHours(10)), Stamp()).Value;

        maintenance.Status.ShouldBe(MaintenanceStatus.Planned);
        maintenance.GetBlock().ShouldBe(P(NextWeek, NextWeek.AddHours(10)));
        maintenance.Cost.ShouldBe(Money.Tnd(180m));
        vehicle.GetMaintenanceBlocks().ShouldBe([P(NextWeek, NextWeek.AddHours(10))]);
        vehicle.DomainEvents.OfType<MaintenanceScheduled>().ShouldHaveSingleItem().Block.ShouldBe(P(NextWeek, NextWeek.AddHours(10)));
        vehicle.DomainEvents.OfType<VehicleAvailabilityChanged>().ShouldHaveSingleItem();
        vehicle.Status.ShouldBe(VehicleStatus.Active);
    }

    [Fact]
    public void Maintenance_blocks_of_a_vehicle_never_overlap_but_may_touch()
    {
        var vehicle = NewVehicle();
        vehicle.ScheduleMaintenance(Maintenance(NextWeek, NextWeek.AddHours(10)), Stamp()).IsSuccess.ShouldBeTrue();

        vehicle.ScheduleMaintenance(Maintenance(NextWeek.AddHours(9), NextWeek.AddHours(12)), Stamp()).Error.ShouldBe(FleetErrors.MaintenanceOverlap);
        vehicle.ScheduleMaintenance(Maintenance(NextWeek.AddHours(-1), NextWeek.AddHours(20)), Stamp()).Error.ShouldBe(FleetErrors.MaintenanceOverlap);
        vehicle.ScheduleMaintenance(Maintenance(NextWeek.AddHours(10), NextWeek.AddHours(12)), Stamp()).IsSuccess.ShouldBeTrue();
        vehicle.ScheduleMaintenance(Maintenance(NextWeek.AddHours(-2), NextWeek), Stamp()).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void A_cancelled_maintenance_frees_its_dates()
    {
        var vehicle = NewVehicle();
        var maintenance = vehicle.ScheduleMaintenance(Maintenance(NextWeek, NextWeek.AddHours(10)), Stamp()).Value;
        vehicle.DequeueDomainEvents();

        vehicle.CancelMaintenance(maintenance.Id, null, Stamp()).Error.ShouldBe(FleetErrors.ReasonRequired);
        vehicle.CancelMaintenance(maintenance.Id, "Pièces indisponibles", Stamp()).IsSuccess.ShouldBeTrue();

        maintenance.Status.ShouldBe(MaintenanceStatus.Cancelled);
        maintenance.CancellationReason.ShouldBe("Pièces indisponibles");
        maintenance.GetBlock().ShouldBeNull();
        vehicle.GetMaintenanceBlocks().ShouldBeEmpty();
        vehicle.ScheduleMaintenance(Maintenance(NextWeek, NextWeek.AddHours(10)), Stamp()).IsSuccess.ShouldBeTrue();
        vehicle.DomainEvents.OfType<MaintenanceCancelled>().ShouldHaveSingleItem();
        vehicle.CancelMaintenance(maintenance.Id, "Encore", Stamp()).Error.ShouldBe(FleetErrors.MaintenanceInvalidTransition);
    }

    [Fact]
    public void Maintenance_details_are_validated()
    {
        var vehicle = NewVehicle();

        vehicle.ScheduleMaintenance(Maintenance(NextWeek, NextWeek), Stamp()).Error.ShouldBe(FleetErrors.MaintenanceBlockInvalid);
        vehicle.ScheduleMaintenance(Maintenance(NextWeek, NextWeek.AddDays(367)), Stamp()).Error.ShouldBe(FleetErrors.MaintenanceBlockInvalid);
        vehicle.ScheduleMaintenance(Maintenance(NextWeek, NextWeek.AddHours(1)) with { Type = (MaintenanceType)0 }, Stamp()).Error.ShouldBe(FleetErrors.MaintenanceTypeInvalid);
        vehicle.ScheduleMaintenance(Maintenance(NextWeek, NextWeek.AddHours(1)) with { Description = new string('d', 501) }, Stamp()).Error.ShouldBe(FleetErrors.MaintenanceTextTooLong);
        vehicle.ScheduleMaintenance(Maintenance(NextWeek, NextWeek.AddHours(1)) with { EstimatedCost = Money.Tnd(-5m) }, Stamp()).Error.ShouldBe(FleetErrors.AmountInvalid);
        vehicle.MaintenanceEvents.ShouldBeEmpty();
    }

    [Fact]
    public void Rescheduling_moves_a_planned_block_without_overlapping_another()
    {
        var vehicle = NewVehicle();
        var first = vehicle.ScheduleMaintenance(Maintenance(NextWeek, NextWeek.AddHours(4)), Stamp()).Value;
        vehicle.ScheduleMaintenance(Maintenance(NextWeek.AddDays(2), NextWeek.AddDays(3)), Stamp()).IsSuccess.ShouldBeTrue();
        vehicle.DequeueDomainEvents();

        vehicle.RescheduleMaintenance(first.Id, NextWeek.AddDays(2).AddHours(-1), NextWeek.AddDays(2).AddHours(1), Stamp()).Error.ShouldBe(FleetErrors.MaintenanceOverlap);
        vehicle.RescheduleMaintenance(first.Id, NextWeek.AddHours(1), NextWeek, Stamp()).Error.ShouldBe(FleetErrors.MaintenanceBlockInvalid);
        vehicle.RescheduleMaintenance(Guid.CreateVersion7(), NextWeek, NextWeek.AddHours(1), Stamp()).Error.ShouldBe(FleetErrors.MaintenanceNotFound);
        vehicle.RescheduleMaintenance(first.Id, NextWeek.AddDays(1), NextWeek.AddDays(1).AddHours(4), Stamp()).IsSuccess.ShouldBeTrue();

        first.GetBlock().ShouldBe(P(NextWeek.AddDays(1), NextWeek.AddDays(1).AddHours(4)));
        vehicle.DomainEvents.OfType<MaintenanceRescheduled>().ShouldHaveSingleItem();
        vehicle.DomainEvents.OfType<VehicleAvailabilityChanged>().ShouldHaveSingleItem();
    }

    [Fact]
    public void Starting_a_maintenance_early_moves_its_block_to_now_and_puts_the_vehicle_in_maintenance()
    {
        var vehicle = NewVehicle();
        var maintenance = vehicle.ScheduleMaintenance(Maintenance(NextWeek, NextWeek.AddHours(10)), Stamp()).Value;
        vehicle.DequeueDomainEvents();

        vehicle.StartMaintenance(maintenance.Id, NextWeek.AddDays(1), Stamp()).IsSuccess.ShouldBeTrue();

        maintenance.Status.ShouldBe(MaintenanceStatus.InProgress);
        maintenance.StartedAtUtc.ShouldBe(Now);
        maintenance.GetBlock().ShouldBe(P(Now, NextWeek.AddDays(1)));
        vehicle.Status.ShouldBe(VehicleStatus.Maintenance);
        vehicle.StatusReason.ShouldBe("Vidange 10 000 km");
        vehicle.HasMaintenanceInProgress.ShouldBeTrue();
        vehicle.DomainEvents.OfType<MaintenanceStarted>().ShouldHaveSingleItem();
        vehicle.DomainEvents.OfType<VehicleStatusChanged>().ShouldHaveSingleItem().Status.ShouldBe(VehicleStatus.Maintenance);
        vehicle.DomainEvents.OfType<VehicleAvailabilityChanged>().ShouldHaveSingleItem();

        // The dated block covers now: the vehicle is available again once it ends.
        vehicle.AvailabilityWindows(P(Now, NextWeek.AddDays(3)), Now).ShouldBe([P(NextWeek.AddDays(1), NextWeek.AddDays(3))]);
        vehicle.ChangeStatus(VehicleStatus.Active, null, Stamp()).Error.ShouldBe(FleetErrors.MaintenanceInProgress);
    }

    [Fact]
    public void A_started_maintenance_needs_an_expected_end_in_the_future_and_no_other_work_in_progress()
    {
        var vehicle = NewVehicle();
        var past = vehicle.ScheduleMaintenance(Maintenance(Now.AddDays(-3), Now.AddDays(-2)), Stamp()).Value;
        var next = vehicle.ScheduleMaintenance(Maintenance(NextWeek, NextWeek.AddHours(10)), Stamp()).Value;
        var later = vehicle.ScheduleMaintenance(Maintenance(NextWeek.AddDays(5), NextWeek.AddDays(6)), Stamp()).Value;

        vehicle.StartMaintenance(past.Id, null, Stamp()).Error.ShouldBe(FleetErrors.MaintenanceEndInPast);
        vehicle.StartMaintenance(next.Id, NextWeek.AddDays(5).AddHours(1), Stamp()).Error.ShouldBe(FleetErrors.MaintenanceOverlap);
        vehicle.StartMaintenance(next.Id, null, Stamp()).IsSuccess.ShouldBeTrue();
        vehicle.StartMaintenance(later.Id, null, Stamp()).Error.ShouldBe(FleetErrors.MaintenanceAlreadyInProgress);
        vehicle.StartMaintenance(next.Id, null, Stamp()).Error.ShouldBe(FleetErrors.MaintenanceInvalidTransition);
    }

    [Fact]
    public void Completing_a_maintenance_frees_the_rest_of_the_block_reactivates_the_vehicle_and_moves_the_plan()
    {
        var vehicle = NewVehicle(mileage: 48_000);
        vehicle.SetMaintenancePlan(new MaintenancePlanDetails(MaintenanceType.OilChange, 10_000, 12, new DateOnly(2025, 10, 1), 40_000), Stamp()).IsSuccess.ShouldBeTrue();
        var maintenance = vehicle.ScheduleMaintenance(Maintenance(Now.AddHours(-2), Now.AddHours(8)), Stamp(Now.AddDays(-1))).Value;
        vehicle.StartMaintenance(maintenance.Id, null, Stamp(Now.AddHours(-1))).IsSuccess.ShouldBeTrue();
        vehicle.DequeueDomainEvents();

        vehicle.CompleteMaintenance(maintenance.Id, null, 48_250, Money.Tnd(195.500m), "  Filtre changé ", Stamp()).IsSuccess.ShouldBeTrue();

        maintenance.Status.ShouldBe(MaintenanceStatus.Completed);
        maintenance.CompletedAtUtc.ShouldBe(Now);
        maintenance.GetBlock().ShouldBe(P(Now.AddHours(-2), Now));
        maintenance.Cost.ShouldBe(Money.Tnd(195.500m));
        maintenance.Mileage.ShouldBe(48_250);
        maintenance.CompletionNotes.ShouldBe("Filtre changé");
        vehicle.Mileage.ShouldBe(48_250);
        vehicle.Status.ShouldBe(VehicleStatus.Active);
        var plan = vehicle.MaintenancePlans.ShouldHaveSingleItem();
        plan.LastDoneOn.ShouldBe(new DateOnly(2026, 10, 20));
        plan.LastDoneMileage.ShouldBe(48_250);
        plan.NextDueMileage.ShouldBe(58_250);
        plan.NextDueOn.ShouldBe(new DateOnly(2027, 10, 20));
        vehicle.DomainEvents.OfType<MaintenanceCompleted>().ShouldHaveSingleItem();
        vehicle.DomainEvents.OfType<VehicleStatusChanged>().ShouldHaveSingleItem().Status.ShouldBe(VehicleStatus.Active);
        vehicle.DomainEvents.OfType<VehicleAvailabilityChanged>().ShouldHaveSingleItem();
        vehicle.AvailabilityWindows(P(Now, Now.AddDays(1)), Now).ShouldBe([P(Now, Now.AddDays(1))]);
    }

    [Fact]
    public void A_past_planned_maintenance_can_be_completed_at_its_actual_end()
    {
        var vehicle = NewVehicle(mileage: 30_000);
        var maintenance = vehicle.ScheduleMaintenance(Maintenance(Now.AddDays(-3), Now.AddDays(-2)), Stamp()).Value;

        vehicle.CompleteMaintenance(maintenance.Id, Now.AddDays(-2), 29_000, null, null, Stamp()).IsSuccess.ShouldBeTrue();

        maintenance.GetBlock().ShouldBe(P(Now.AddDays(-3), Now.AddDays(-2)));
        maintenance.StartedAtUtc.ShouldBe(Now.AddDays(-3));
        vehicle.Mileage.ShouldBe(30_000);
        vehicle.Status.ShouldBe(VehicleStatus.Active);
    }

    [Fact]
    public void A_completion_comes_after_the_start_and_not_in_the_future()
    {
        var vehicle = NewVehicle();
        var maintenance = vehicle.ScheduleMaintenance(Maintenance(NextWeek, NextWeek.AddHours(10)), Stamp()).Value;

        vehicle.CompleteMaintenance(maintenance.Id, null, null, null, null, Stamp()).Error.ShouldBe(FleetErrors.MaintenanceNotStarted);
        vehicle.CompleteMaintenance(maintenance.Id, NextWeek.AddHours(1), null, null, null, Stamp()).Error.ShouldBe(FleetErrors.MaintenanceNotStarted);
        vehicle.CompleteMaintenance(Guid.CreateVersion7(), null, null, null, null, Stamp()).Error.ShouldBe(FleetErrors.MaintenanceNotFound);

        vehicle.CompleteMaintenance(maintenance.Id, NextWeek.AddHours(1), -1, null, null, Stamp(NextWeek.AddHours(2))).Error.ShouldBe(FleetErrors.MileageInvalid);
        vehicle.CompleteMaintenance(maintenance.Id, NextWeek.AddHours(1), null, Money.Tnd(-1m), null, Stamp(NextWeek.AddHours(2))).Error.ShouldBe(FleetErrors.AmountInvalid);
        vehicle.CompleteMaintenance(maintenance.Id, NextWeek.AddHours(1), null, null, null, Stamp(NextWeek.AddHours(2))).IsSuccess.ShouldBeTrue();
        vehicle.CompleteMaintenance(maintenance.Id, null, null, null, null, Stamp(NextWeek.AddHours(3))).Error.ShouldBe(FleetErrors.MaintenanceInvalidTransition);
    }

    [Fact]
    public void Completing_the_maintenance_of_an_out_of_service_vehicle_keeps_its_status()
    {
        var vehicle = NewVehicle();
        var maintenance = vehicle.ScheduleMaintenance(Maintenance(Now.AddHours(-2), Now.AddHours(8)), Stamp()).Value;
        vehicle.StartMaintenance(maintenance.Id, null, Stamp()).IsSuccess.ShouldBeTrue();
        vehicle.ChangeStatus(VehicleStatus.OutOfService, "Accident pendant l'essai", Stamp()).IsSuccess.ShouldBeTrue();

        vehicle.CompleteMaintenance(maintenance.Id, null, null, null, null, Stamp(Now.AddHours(1))).IsSuccess.ShouldBeTrue();

        vehicle.Status.ShouldBe(VehicleStatus.OutOfService);
    }

    [Fact]
    public void A_maintenance_in_progress_prevents_the_sale()
    {
        var vehicle = NewVehicle();
        var maintenance = vehicle.ScheduleMaintenance(Maintenance(Now.AddHours(-2), Now.AddHours(8)), Stamp()).Value;
        vehicle.StartMaintenance(maintenance.Id, null, Stamp()).IsSuccess.ShouldBeTrue();

        vehicle.Sell(Now, Money.Tnd(1m), null, Stamp()).Error.ShouldBe(FleetErrors.MaintenanceInProgress);
    }

    [Fact]
    public void Maintenance_plans_are_unique_per_type_and_validated()
    {
        var vehicle = NewVehicle(mileage: 25_000);

        var plan = vehicle.SetMaintenancePlan(new MaintenancePlanDetails(MaintenanceType.OilChange, 10_000, null, Label: " Vidange "), Stamp()).Value;
        plan.LastDoneMileage.ShouldBe(25_000);
        plan.LastDoneOn.ShouldBe(new DateOnly(2026, 10, 20));
        plan.Label.ShouldBe("Vidange");
        vehicle.SetMaintenancePlan(new MaintenancePlanDetails(MaintenanceType.OilChange, 15_000, 12), Stamp()).Value.ShouldBe(plan);
        plan.IntervalKm.ShouldBe(15_000);
        vehicle.MaintenancePlans.ShouldHaveSingleItem();

        vehicle.SetMaintenancePlan(new MaintenancePlanDetails(MaintenanceType.Tyres, null, null), Stamp()).Error.ShouldBe(FleetErrors.MaintenancePlanInvalid);
        vehicle.SetMaintenancePlan(new MaintenancePlanDetails(MaintenanceType.Tyres, 50, null), Stamp()).Error.ShouldBe(FleetErrors.MaintenancePlanInvalid);
        vehicle.SetMaintenancePlan(new MaintenancePlanDetails(MaintenanceType.Tyres, null, 121), Stamp()).Error.ShouldBe(FleetErrors.MaintenancePlanInvalid);
        vehicle.SetMaintenancePlan(new MaintenancePlanDetails((MaintenanceType)77, 1000, null), Stamp()).Error.ShouldBe(FleetErrors.MaintenanceTypeInvalid);
        vehicle.RemoveMaintenancePlan(Guid.CreateVersion7(), Stamp()).Error.ShouldBe(FleetErrors.MaintenancePlanNotFound);
        vehicle.RemoveMaintenancePlan(plan.Id, Stamp()).IsSuccess.ShouldBeTrue();
        vehicle.MaintenancePlans.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(40_000, "2026-01-01", MaintenanceDueState.Ok)]
    [InlineData(49_000, "2026-01-01", MaintenanceDueState.DueSoon)]
    [InlineData(50_000, "2026-01-01", MaintenanceDueState.Overdue)]
    [InlineData(40_000, "2025-11-10", MaintenanceDueState.DueSoon)]
    [InlineData(40_000, "2025-10-20", MaintenanceDueState.DueSoon)]
    [InlineData(40_000, "2025-10-19", MaintenanceDueState.Overdue)]
    public void The_due_state_is_reached_by_the_distance_or_the_date_whichever_comes_first(int mileage, string lastDone, MaintenanceDueState expected)
    {
        // Every 10 000 km or 12 months, last done at 40 000 km; today is 20/10/2026.
        var vehicle = NewVehicle(mileage: 40_000);
        vehicle.SetMaintenancePlan(new MaintenancePlanDetails(MaintenanceType.OilChange, 10_000, 12, DateOnly.Parse(lastDone, System.Globalization.CultureInfo.InvariantCulture), 40_000), Stamp()).IsSuccess.ShouldBeTrue();
        if (mileage > vehicle.Mileage)
        {
            vehicle.RecordMileage(mileage, Stamp()).IsSuccess.ShouldBeTrue();
        }

        var (_, due) = vehicle.MaintenanceDues(new DateOnly(2026, 10, 20), MaintenanceDueMargins.Default).ShouldHaveSingleItem();

        due.State.ShouldBe(expected);
        due.NextDueMileage.ShouldBe(50_000);
    }

    [Fact]
    public void A_plan_by_date_only_reports_the_remaining_days()
    {
        var vehicle = NewVehicle();
        var plan = vehicle.SetMaintenancePlan(new MaintenancePlanDetails(MaintenanceType.Brakes, null, 6, new DateOnly(2026, 6, 1)), Stamp()).Value;

        var due = plan.ComputeDue(vehicle.Mileage, new DateOnly(2026, 10, 20), new MaintenanceDueMargins(500, 10));

        due.NextDueOn.ShouldBe(new DateOnly(2026, 12, 1));
        due.RemainingDays.ShouldBe(42);
        due.RemainingKm.ShouldBeNull();
        due.State.ShouldBe(MaintenanceDueState.Ok);
    }
}
