using CarRental.Application.Bookings;
using CarRental.Application.Bookings.Administration;
using CarRental.Application.Identity.Contracts;
using CarRental.Application.Tests.Bookings.Fakes;
using CarRental.Domain.Bookings;
using CarRental.Domain.Bookings.Availability;
using CarRental.Domain.Common;

namespace CarRental.Application.Tests.Bookings;

/// <summary>Assignment through the guard, compliance waiver of a manager, swap during the rental, greedy proposals.</summary>
public sealed class VehicleAssignmentServiceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_assignment_is_checked_by_the_guard_under_the_lock()
    {
        var harness = new BookingsHarness();
        var booking = await harness.ConfirmedAsync();

        (await harness.Assignments.AssignAsync(booking.Id, harness.VehicleA.Id, cancellationToken: Ct)).IsSuccess.ShouldBeTrue();

        var check = harness.Guard.AssignmentChecks.ShouldHaveSingleItem();
        check.VehicleId.ShouldBe(harness.VehicleA.Id);
        check.CategoryId.ShouldBe(harness.Ecmr.Id);
        check.Booking.BookingId.ShouldBe(booking.Id);
        check.ComplianceWaived.ShouldBeFalse();
        booking.AssignedVehicleId.ShouldBe(harness.VehicleA.Id);
        harness.Journal.Entries.Skip(harness.JournalStart).ShouldBe(["begin", $"guard.vehicle {harness.VehicleA.Id}", "commit", $"notify {harness.Ecmr.Id}"]);
    }

    [Fact]
    public async Task An_overlap_is_refused_and_nothing_changes()
    {
        var harness = new BookingsHarness();
        var booking = await harness.ConfirmedAsync();
        harness.Guard.NextAssignment = AvailabilityErrors.VehicleOverlap;

        (await harness.Assignments.AssignAsync(booking.Id, harness.VehicleA.Id, cancellationToken: Ct)).Error.ShouldBe(AvailabilityErrors.VehicleOverlap);
        booking.AssignedVehicleId.ShouldBeNull();
        harness.Notifier.Changed.ShouldBeEmpty();
    }

    [Fact]
    public async Task Only_a_manager_waives_the_documents_of_a_vehicle_and_the_waiver_is_traced()
    {
        var harness = new BookingsHarness();
        var booking = await harness.ConfirmedAsync();

        (await harness.Assignments.AssignAsync(booking.Id, harness.VehicleA.Id, "Assurance renouvelée", cancellationToken: Ct)).Error.ShouldBe(BookingErrors.Forbidden);
        harness.As(Roles.Manager);
        (await harness.Assignments.AssignAsync(booking.Id, harness.VehicleA.Id, "Assurance renouvelée, attestation demain", cancellationToken: Ct)).IsSuccess.ShouldBeTrue();

        harness.Guard.AssignmentChecks.ShouldHaveSingleItem().ComplianceWaived.ShouldBeTrue();
        booking.ComplianceWaiver.ShouldNotBeNull().Reason.ShouldBe("Assurance renouvelée, attestation demain");
        booking.ComplianceWaiver.ByName.ShouldBe("manager@tests.local");
    }

    [Fact]
    public async Task A_swap_during_the_rental_needs_a_reason_and_is_checked_from_now_on()
    {
        var harness = new BookingsHarness();
        var booking = await harness.ConfirmedAsync();
        (await harness.Assignments.AssignAsync(booking.Id, harness.VehicleA.Id, cancellationToken: Ct)).IsSuccess.ShouldBeTrue();
        harness.Clock.SetUtcNow(harness.PickupAt);
        harness.As(Roles.Preparer);
        (await harness.Lifecycle.StartRentalAsync(booking.Id, cancellationToken: Ct)).IsSuccess.ShouldBeTrue();
        harness.As(Roles.Agent);
        harness.Reset();

        (await harness.Assignments.AssignAsync(booking.Id, harness.VehicleB.Id, cancellationToken: Ct)).Error.ShouldBe(BookingErrors.ReasonRequired);
        (await harness.Assignments.AssignAsync(booking.Id, harness.VehicleB.Id, swapReason: "Panne de climatisation", cancellationToken: Ct)).IsSuccess.ShouldBeTrue();

        harness.Guard.AssignmentChecks[^1].Booking.Kind.ShouldBe(OccupancyKind.InProgress);
        booking.AssignedVehicleId.ShouldBe(harness.VehicleB.Id);
        booking.Events[^1].Type.ShouldBe(BookingEventType.VehicleSwapped);
        (await harness.Assignments.UnassignAsync(booking.Id, Ct)).Error.ShouldBe(BookingErrors.InvalidTransition);
    }

    [Fact]
    public async Task Unassigning_is_a_command_of_the_file()
    {
        var harness = new BookingsHarness();
        var booking = await harness.ConfirmedAsync();
        (await harness.Assignments.AssignAsync(booking.Id, harness.VehicleA.Id, cancellationToken: Ct)).IsSuccess.ShouldBeTrue();

        (await harness.Assignments.UnassignAsync(booking.Id, Ct)).IsSuccess.ShouldBeTrue();
        booking.AssignedVehicleId.ShouldBeNull();
        harness.As(Roles.Accountant);
        (await harness.Assignments.UnassignAsync(booking.Id, Ct)).Error.ShouldBe(BookingErrors.Forbidden);
    }

    [Fact]
    public async Task The_greedy_assigner_proposes_the_vehicle_freed_the_earliest_and_proposals_are_applied_one_by_one()
    {
        var harness = new BookingsHarness();
        var first = await harness.ConfirmedAsync();
        var second = await harness.ConfirmedAsync(harness.Quick() with { PickupAt = harness.PickupAt.AddDays(1), ReturnAt = harness.ReturnAt.AddDays(1) });
        var range = Period.Create(harness.PickupAt.AddDays(-1), harness.PickupAt.AddDays(10)).Value;

        var plan = await harness.Assignments.ProposeAsync(harness.AgencyId, range, Ct);

        plan.IsSuccess.ShouldBeTrue(plan.IsFailure ? plan.Error.ToString() : null);
        plan.Value.Proposals.Select(p => (p.BookingNumber, p.PlateNumber)).ShouldBe(
        [
            (first.Number, harness.VehicleA.PlateNumber),
            (second.Number, harness.VehicleB.PlateNumber),
        ]);
        plan.Value.Unassignable.ShouldBeEmpty();

        harness.Guard.NextAssignment = Result.Success();
        var applied = await harness.Assignments.ApplyAsync([.. plan.Value.Proposals.Select(p => new AssignmentChoice(p.BookingId, p.VehicleId))], Ct);
        applied.Value.ShouldAllBe(outcome => outcome.IsSuccess);
        first.AssignedVehicleId.ShouldBe(harness.VehicleA.Id);
        second.AssignedVehicleId.ShouldBe(harness.VehicleB.Id);
    }

    [Fact]
    public async Task Files_still_out_at_the_end_of_the_range_are_planned_for_their_whole_period()
    {
        var harness = new BookingsHarness();
        var fixedFile = await harness.ConfirmedAsync();
        (await harness.Assignments.AssignAsync(fixedFile.Id, harness.VehicleA.Id, cancellationToken: Ct)).IsSuccess.ShouldBeTrue();
        var crossing = await harness.ConfirmedAsync(harness.Quick() with { PickupAt = harness.PickupAt.AddDays(1), ReturnAt = harness.ReturnAt.AddDays(1) });

        // The week shown on the planning ends while both files are out (the fleet clips the windows to the period asked).
        var week = Period.Create(harness.PickupAt.AddDays(-1), harness.PickupAt.AddDays(2)).Value;
        var plan = await harness.Assignments.ProposeAsync(harness.AgencyId, week, Ct);

        plan.IsSuccess.ShouldBeTrue(plan.IsFailure ? plan.Error.ToString() : null);
        plan.Value.Conflicts.ShouldBeEmpty("the vehicle of the fixed file is rentable until its return, after the week");
        plan.Value.Unassignable.ShouldBeEmpty();
        var proposal = plan.Value.Proposals.ShouldHaveSingleItem();
        proposal.BookingId.ShouldBe(crossing.Id);
        proposal.VehicleId.ShouldBe(harness.VehicleB.Id, "vehicle A carries the fixed file until its return");
    }

    [Fact]
    public async Task A_file_after_the_range_still_keeps_its_vehicle_from_the_proposals()
    {
        var harness = new BookingsHarness();
        var fixedFile = await harness.ConfirmedAsync();
        (await harness.Assignments.AssignAsync(fixedFile.Id, harness.VehicleA.Id, cancellationToken: Ct)).IsSuccess.ShouldBeTrue();
        var crossing = await harness.ConfirmedAsync(harness.Quick() with { PickupAt = harness.PickupAt.AddDays(1), ReturnAt = harness.ReturnAt.AddDays(1) });
        var later = await harness.ConfirmedAsync(harness.Quick() with { PickupAt = harness.PickupAt.AddDays(3), ReturnAt = harness.ReturnAt.AddDays(3) });
        (await harness.Assignments.AssignAsync(later.Id, harness.VehicleB.Id, cancellationToken: Ct)).IsSuccess.ShouldBeTrue();

        var week = Period.Create(harness.PickupAt.AddDays(-1), harness.PickupAt.AddDays(2)).Value;
        var plan = await harness.Assignments.ProposeAsync(harness.AgencyId, week, Ct);

        plan.Value.Proposals.ShouldBeEmpty("vehicle B leaves again before the crossing file is back");
        plan.Value.Unassignable.ShouldHaveSingleItem().BookingId.ShouldBe(crossing.Id);
        plan.Value.Conflicts.ShouldBeEmpty("the file after the week is not part of the proposals");
    }

    [Fact]
    public async Task Candidates_are_pre_checked_for_the_assignment_dialog()
    {
        var harness = new BookingsHarness();
        var taken = await harness.ConfirmedAsync();
        (await harness.Assignments.AssignAsync(taken.Id, harness.VehicleA.Id, cancellationToken: Ct)).IsSuccess.ShouldBeTrue();
        var booking = await harness.ConfirmedAsync();

        var candidates = await harness.Assignments.GetCandidatesAsync(booking.Id, Ct);

        candidates.Value.Single(c => c.VehicleId == harness.VehicleA.Id).State.ShouldBe(VehicleCandidateState.Overlap);
        candidates.Value.Single(c => c.VehicleId == harness.VehicleB.Id).State.ShouldBe(VehicleCandidateState.Free);
    }
}
