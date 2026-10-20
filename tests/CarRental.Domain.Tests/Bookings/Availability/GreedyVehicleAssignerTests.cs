using CarRental.Domain.Bookings.Availability;
using static CarRental.Domain.Tests.Bookings.Availability.AvailabilityTestData;

namespace CarRental.Domain.Tests.Bookings.Availability;

/// <summary>
/// Greedy assignment (docs/conception §3): fixed assignments stay, the others are taken by start and each gets, among
/// the vehicles free over its whole interval, the one freed the earliest (tie-break: plate number).
/// </summary>
public sealed class GreedyVehicleAssignerTests
{
    [Fact]
    public void Bookings_are_spread_over_the_free_vehicles()
    {
        var plan = GreedyVehicleAssigner.Assign(
            [AlwaysRentable(1), AlwaysRentable(2)],
            [Booking(1, P(0, 10)), Booking(2, P(5, 15)), Booking(3, P(12, 20))]);

        plan.Proposals.ShouldBe(
        [
            new VehicleAssignmentProposal(BookingId(1), VehicleId(1)),
            new VehicleAssignmentProposal(BookingId(2), VehicleId(2)),
            new VehicleAssignmentProposal(BookingId(3), VehicleId(1)),
        ]);
        plan.Unassignable.ShouldBeEmpty();
        plan.Conflicts.ShouldBeEmpty();
        plan.IsComplete.ShouldBeTrue();
    }

    [Fact]
    public void The_vehicle_freed_the_earliest_is_chosen()
    {
        // Vehicle 1 is free from 10 h, vehicle 2 from 4 h: the booking at 20 h takes vehicle 2.
        var plan = GreedyVehicleAssigner.Assign(
            [AlwaysRentable(1), AlwaysRentable(2)],
            [Booking(1, P(0, 10), vehicle: 1), Booking(2, P(0, 4), vehicle: 2), Booking(3, P(20, 30))]);

        plan.Proposals.ShouldBe([new VehicleAssignmentProposal(BookingId(3), VehicleId(2))]);
    }

    [Fact]
    public void Equally_free_vehicles_are_chosen_by_plate_number()
    {
        var plan = GreedyVehicleAssigner.Assign(
            [
                new RentableVehicle(VehicleId(1), "200 TU 1000", [Always]),
                new RentableVehicle(VehicleId(2), "100 TU 9999", [Always]),
            ],
            [Booking(1, P(0, 10))]);

        plan.Proposals.ShouldBe([new VehicleAssignmentProposal(BookingId(1), VehicleId(2))]);
    }

    [Fact]
    public void A_vehicle_never_used_counts_as_freed_when_its_window_opened()
    {
        // Vehicle 1 entered service at 15 h; vehicle 2 was returned (buffer included) at 12 h.
        var plan = GreedyVehicleAssigner.Assign(
            [Vehicle(1, P(15, 1000)), AlwaysRentable(2)],
            [Booking(1, P(0, 12), vehicle: 2), Booking(2, P(20, 30))]);

        plan.Proposals.ShouldBe([new VehicleAssignmentProposal(BookingId(2), VehicleId(2))]);
    }

    [Fact]
    public void Fixed_assignments_are_kept_and_block_their_vehicle_buffer_included()
    {
        var plan = GreedyVehicleAssigner.Assign(
            [AlwaysRentable(1), AlwaysRentable(2)],
            [Booking(1, P(0, 12), vehicle: 1), Booking(2, P(11, 20))]);

        plan.Proposals.ShouldBe([new VehicleAssignmentProposal(BookingId(2), VehicleId(2))]);
    }

    [Fact]
    public void A_guaranteed_model_keeps_its_pinned_vehicle()
    {
        // Bookings 1 and 2 carry the vehicle chosen by the customer ("guaranteed model"): they stay on vehicle 2,
        // which only takes other bookings between them.
        var plan = GreedyVehicleAssigner.Assign(
            [AlwaysRentable(1), AlwaysRentable(2)],
            [Booking(1, P(0, 5), vehicle: 2), Booking(2, P(50, 60), vehicle: 2), Booking(3, P(10, 20))]);

        plan.Proposals.ShouldBe([new VehicleAssignmentProposal(BookingId(3), VehicleId(1))]);
        plan.Conflicts.ShouldBeEmpty();
    }

    [Fact]
    public void A_vehicle_must_be_rentable_over_the_whole_interval()
    {
        // Vehicle 1 has a maintenance block [8 h, 9 h); vehicle 2 is sold at 15 h.
        var plan = GreedyVehicleAssigner.Assign(
            [Vehicle(1, P(-100, 8), P(9, 100)), Vehicle(2, P(-100, 15)), AlwaysRentable(3)],
            [Booking(1, P(0, 20))]);

        plan.Proposals.ShouldBe([new VehicleAssignmentProposal(BookingId(1), VehicleId(3))]);
    }

    [Fact]
    public void Unassignable_bookings_say_why()
    {
        var plan = GreedyVehicleAssigner.Assign(
            [Vehicle(1, P(0, 100))],
            [Booking(1, P(0, 10), vehicle: 1), Booking(2, P(5, 8)), Booking(3, P(90, 110))]);

        plan.Proposals.ShouldBeEmpty();
        plan.Unassignable.ShouldBe(
        [
            new UnassignableBooking(BookingId(2), AvailabilityErrors.AllVehiclesOccupied),
            new UnassignableBooking(BookingId(3), AvailabilityErrors.NoVehicleInService),
        ]);
        plan.IsComplete.ShouldBeFalse();
    }

    [Fact]
    public void Fixed_assignments_that_break_the_rules_are_reported()
    {
        var plan = GreedyVehicleAssigner.Assign(
            [AlwaysRentable(1)],
            [Booking(1, P(0, 10), vehicle: 1), Booking(2, P(5, 15), vehicle: 1), Booking(3, P(0, 5), vehicle: 9)]);

        plan.Conflicts.ShouldBe(
        [
            new AssignmentConflict(BookingId(3), VehicleId(9), AvailabilityErrors.VehicleNotInCategory),
            new AssignmentConflict(BookingId(2), VehicleId(1), AvailabilityErrors.VehicleOverlap),
        ]);
        plan.IsComplete.ShouldBeFalse();
    }

    [Fact]
    public void A_fixed_assignment_on_a_vehicle_no_longer_rentable_is_reported()
    {
        // Vehicle 1 got a maintenance block [8 h, 9 h) after booking 1 was assigned to it.
        var plan = GreedyVehicleAssigner.Assign(
            [Vehicle(1, P(-100, 8), P(9, 100)), AlwaysRentable(2)],
            [Booking(1, P(0, 20), vehicle: 1), Booking(2, P(0, 20), vehicle: 2)]);

        plan.Conflicts.ShouldBe([new AssignmentConflict(BookingId(1), VehicleId(1), AvailabilityErrors.VehicleUnavailable)]);
        plan.IsComplete.ShouldBeFalse();
    }

    [Fact]
    public void A_fixed_assignment_reports_a_single_conflict_overlap_first()
    {
        var plan = GreedyVehicleAssigner.Assign(
            [Vehicle(1, P(-100, 8), P(9, 100))],
            [Booking(1, P(0, 5), vehicle: 1), Booking(2, P(4, 20), vehicle: 1)]);

        plan.Conflicts.ShouldBe([new AssignmentConflict(BookingId(2), VehicleId(1), AvailabilityErrors.VehicleOverlap)]);
    }

    [Fact]
    public void Proposals_never_overlap_a_conflicting_fixed_assignment()
    {
        var plan = GreedyVehicleAssigner.Assign(
            [AlwaysRentable(1), AlwaysRentable(2)],
            [Booking(1, P(0, 10), vehicle: 1), Booking(2, P(5, 15), vehicle: 1), Booking(3, P(12, 14))]);

        plan.Proposals.ShouldBe([new VehicleAssignmentProposal(BookingId(3), VehicleId(2))]);
    }

    [Fact]
    public void The_past_can_be_ignored_for_a_vehicle_swap_during_a_rental()
    {
        // Booking 1 moved to vehicle 2 at 30 h while vehicle 2 carried booking 2 until 25 h: no conflict from now on.
        var intervals = new[] { Booking(1, P(0, 50), vehicle: 2), Booking(2, P(10, 25), vehicle: 2), Booking(3, P(20, 40)) };

        var plan = GreedyVehicleAssigner.Assign([AlwaysRentable(1), AlwaysRentable(2)], intervals, notBefore: At(30));

        plan.Conflicts.ShouldBeEmpty();
        plan.Proposals.ShouldBe([new VehicleAssignmentProposal(BookingId(3), VehicleId(1))]);
        GreedyVehicleAssigner.Assign([AlwaysRentable(1), AlwaysRentable(2)], intervals).Conflicts.ShouldHaveSingleItem();
    }

    [Fact]
    public void Bookings_ending_before_the_cut_off_are_ignored()
    {
        var plan = GreedyVehicleAssigner.Assign([AlwaysRentable(1)], [Booking(1, P(0, 10))], notBefore: At(10));

        plan.Proposals.ShouldBeEmpty();
        plan.Unassignable.ShouldBeEmpty();
    }

    [Fact]
    public void Bookings_starting_together_are_taken_by_end_then_id()
    {
        var plan = GreedyVehicleAssigner.Assign(
            [AlwaysRentable(1), AlwaysRentable(2), AlwaysRentable(3)],
            [Booking(3, P(0, 10)), Booking(2, P(0, 10)), Booking(1, P(0, 5))]);

        plan.Proposals.Select(p => p.BookingId).ShouldBe([BookingId(1), BookingId(2), BookingId(3)]);
        plan.Proposals.Select(p => p.VehicleId).ShouldBe([VehicleId(1), VehicleId(2), VehicleId(3)]);
    }

    [Fact]
    public void A_booking_listed_twice_is_a_bug()
    {
        Should.Throw<ArgumentException>(() =>
            GreedyVehicleAssigner.Assign([AlwaysRentable(1)], [Booking(1, P(0, 10)), Booking(1, P(20, 30))]));
        Should.Throw<ArgumentNullException>(() => GreedyVehicleAssigner.Assign(null!, []));
        Should.Throw<ArgumentNullException>(() => GreedyVehicleAssigner.Assign([], null!));
        Should.Throw<ArgumentNullException>(() => GreedyVehicleAssigner.Assign([], [null!]));
    }

    [Fact]
    public void Nothing_to_assign_gives_an_empty_plan()
    {
        var plan = GreedyVehicleAssigner.Assign([], []);

        plan.Proposals.ShouldBeEmpty();
        plan.Unassignable.ShouldBeEmpty();
        plan.Conflicts.ShouldBeEmpty();
        plan.IsComplete.ShouldBeTrue();
    }
}
