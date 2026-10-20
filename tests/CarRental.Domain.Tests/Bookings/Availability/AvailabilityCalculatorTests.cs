using CarRental.Domain.Bookings.Availability;
using static CarRental.Domain.Tests.Bookings.Availability.AvailabilityTestData;

namespace CarRental.Domain.Tests.Bookings.Availability;

/// <summary>A request <c>[s, e + buffer)</c> is available when occupancy(t) + 1 ≤ capacity(t) at every instant.</summary>
public sealed class AvailabilityCalculatorTests
{
    private static AvailabilityCalculator Calculator(
        IEnumerable<RentableVehicle> vehicles,
        IEnumerable<OccupancyInterval> occupancy,
        ChannelRestrictions? restrictions = null) => new(vehicles, occupancy, restrictions);

    [Fact]
    public void A_period_is_available_while_occupancy_plus_one_fits_the_capacity()
    {
        var calculator = Calculator([AlwaysRentable(1), AlwaysRentable(2)], [Booking(1, P(0, 50))]);

        var check = calculator.Check(P(10, 20), TwoHours);

        check.IsAvailable.ShouldBeTrue();
        check.Occupancy.ShouldBe(P(10, 22));
        check.MinimumRemaining.ShouldBe(1);
        check.FirstConflict.ShouldBeNull();
        check.ConflictReason.ShouldBe(AvailabilityConflictReason.None);
        check.ToResult().IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void The_last_vehicle_cannot_be_sold_twice()
    {
        var calculator = Calculator([AlwaysRentable(1)], [Booking(1, P(0, 50))]);

        var check = calculator.Check(P(10, 20), TwoHours);

        check.IsAvailable.ShouldBeFalse();
        check.FirstConflict.ShouldBe(P(10, 22));
        check.MinimumRemaining.ShouldBe(0);
        check.ConflictReason.ShouldBe(AvailabilityConflictReason.Capacity);
        check.ToResult().Error.ShouldBe(AvailabilityErrors.NotAvailable);
    }

    [Fact]
    public void Touching_bounds_do_not_conflict_and_the_existing_buffer_is_respected()
    {
        // Existing rental [0 h, 10 h) occupies the only vehicle until 12 h (2 h buffer).
        var calculator = Calculator([AlwaysRentable(1)], [Booking(1, P(0, 12))]);

        calculator.Check(P(12, 20), TwoHours).IsAvailable.ShouldBeTrue();
        var oneMinuteEarly = calculator.Check(P(12 - (1 / 60d), 20), TwoHours);
        oneMinuteEarly.IsAvailable.ShouldBeFalse();
        oneMinuteEarly.FirstConflict.ShouldBe(P(12 - (1 / 60d), 12));

        // A request whose own buffer ends exactly at the existing pick-up fits.
        calculator.Check(P(-5, -2), TwoHours).IsAvailable.ShouldBeTrue();
        calculator.Check(P(-5, -1), TwoHours).FirstConflict.ShouldBe(P(0, 1));
    }

    [Fact]
    public void The_buffer_of_the_request_must_end_before_the_next_pickup()
    {
        var calculator = Calculator([AlwaysRentable(1)], [Booking(1, P(24, 30))]);

        var check = calculator.Check(P(10, 23), TwoHours);

        check.Occupancy.ShouldBe(P(10, 25));
        check.FirstConflict.ShouldBe(P(24, 25));
        calculator.Check(P(10, 22), TwoHours).IsAvailable.ShouldBeTrue();
        calculator.Check(P(10, 23), TimeSpan.FromHours(1)).IsAvailable.ShouldBeTrue();
    }

    [Fact]
    public void A_maintenance_block_makes_the_period_unavailable_exactly_during_the_block()
    {
        var calculator = Calculator([Vehicle(1, P(0, 20), P(26, 100))], []);

        var check = calculator.Check(P(10, 30), TimeSpan.Zero);

        check.FirstConflict.ShouldBe(P(20, 26));
        check.ConflictReason.ShouldBe(AvailabilityConflictReason.Capacity);
        check.MinimumRemaining.ShouldBe(0);
        calculator.Check(P(0, 18), TwoHours).IsAvailable.ShouldBeTrue();
        calculator.Check(P(0, 19), TwoHours).FirstConflict.ShouldBe(P(20, 21));
    }

    [Fact]
    public void A_capacity_change_in_the_middle_of_the_period_is_detected()
    {
        // Vehicle 2 is sold at 30 h; vehicle 3 enters service at 35 h.
        var vehicles = new[] { AlwaysRentable(1), Vehicle(2, P(-1000, 30)), Vehicle(3, P(35, 1000)) };
        var calculator = Calculator(vehicles, [Booking(1, P(0, 100))]);

        var check = calculator.Check(P(10, 40), TwoHours);

        check.IsAvailable.ShouldBeFalse();
        check.FirstConflict.ShouldBe(P(30, 35));
        calculator.Check(P(10, 28), TwoHours).IsAvailable.ShouldBeTrue();
        calculator.Check(P(35, 60), TwoHours).IsAvailable.ShouldBeTrue();
    }

    [Fact]
    public void Quotas_keep_vehicles_for_the_other_channels()
    {
        var vehicles = new[] { AlwaysRentable(1), AlwaysRentable(2), AlwaysRentable(3) };
        var occupancy = new[] { Booking(1, P(0, 100)) };
        var keepTwoForOthers = new ChannelRestrictions([new ReservedCapacity(P(0, 50), 2)], []);

        var restricted = Calculator(vehicles, occupancy, keepTwoForOthers);
        var direct = Calculator(vehicles, occupancy);

        var check = restricted.Check(P(10, 20), TwoHours);
        check.IsAvailable.ShouldBeFalse();
        check.ConflictReason.ShouldBe(AvailabilityConflictReason.Quota);
        direct.Check(P(10, 20), TwoHours).MinimumRemaining.ShouldBe(2);
        restricted.Check(P(60, 70), TwoHours).IsAvailable.ShouldBeTrue();
        restricted.Capacity.ValueAt(At(10)).ShouldBe(1);
        restricted.FleetCapacity.ValueAt(At(10)).ShouldBe(3);
    }

    [Fact]
    public void A_stop_sell_closes_the_channel_whatever_the_capacity()
    {
        var calculator = Calculator(
            [AlwaysRentable(1), AlwaysRentable(2), AlwaysRentable(3)],
            [],
            new ChannelRestrictions([], [P(10, 12)]));

        var check = calculator.Check(P(0, 20), TwoHours);

        check.FirstConflict.ShouldBe(P(10, 12));
        check.ConflictReason.ShouldBe(AvailabilityConflictReason.StopSale);
        calculator.Check(P(12, 20), TwoHours).IsAvailable.ShouldBeTrue();
    }

    [Fact]
    public void The_reason_of_a_conflict_prefers_capacity_then_stop_sell_then_quota()
    {
        var vehicle = new[] { AlwaysRentable(1) };
        var everything = new ChannelRestrictions([new ReservedCapacity(P(0, 100), 1)], [P(0, 20)]);

        Calculator(vehicle, [Booking(1, P(0, 10))], everything).Check(P(2, 5), TimeSpan.Zero)
            .ConflictReason.ShouldBe(AvailabilityConflictReason.Capacity);
        Calculator(vehicle, [], everything).Check(P(2, 5), TimeSpan.Zero)
            .ConflictReason.ShouldBe(AvailabilityConflictReason.StopSale);
        Calculator(vehicle, [], everything).Check(P(30, 35), TimeSpan.Zero)
            .ConflictReason.ShouldBe(AvailabilityConflictReason.Quota);
    }

    [Fact]
    public void An_amendment_does_not_count_the_amended_booking()
    {
        var calculator = Calculator([AlwaysRentable(1)], [Booking(1, P(0, 50)), Booking(2, P(60, 70))]);

        calculator.Check(P(5, 38), TwoHours).IsAvailable.ShouldBeFalse();
        calculator.Check(P(5, 38), TwoHours, BookingId(1)).IsAvailable.ShouldBeTrue();
        calculator.Check(P(0, 48), TwoHours, BookingId(1)).MinimumRemaining.ShouldBe(1);

        // Extending booking 1 until 80 h collides with booking 2.
        calculator.Check(P(0, 80), TwoHours, BookingId(1)).FirstConflict.ShouldBe(P(60, 70));
    }

    [Fact]
    public void Excluding_an_unknown_booking_changes_nothing()
    {
        var calculator = Calculator([AlwaysRentable(1)], [Booking(1, P(0, 50))]);

        calculator.Check(P(5, 10), TwoHours, BookingId(99)).ShouldBe(calculator.Check(P(5, 10), TwoHours));
    }

    [Fact]
    public void Every_interval_of_the_excluded_booking_is_left_out()
    {
        var calculator = Calculator([AlwaysRentable(1)], [Booking(1, P(0, 10)), Booking(1, P(20, 30))]);

        calculator.CheckOccupancy(P(0, 30), BookingId(1)).IsAvailable.ShouldBeTrue();
        calculator.CheckOccupancy(P(0, 30)).FirstConflict.ShouldBe(P(0, 10));
    }

    [Fact]
    public void An_overdue_return_keeps_blocking_the_next_rental_until_now_plus_buffer()
    {
        // Planned return at 48 h, still out at 53 h.
        var basis = new OccupancyBasis(At(53), TwoHours);
        var overdue = basis.IntervalOf(new BookingOccupancy(BookingId(1), OccupancyKind.InProgress, P(0, 48), At(1)))!;
        var calculator = Calculator([AlwaysRentable(1)], [overdue]);

        calculator.Check(P(54, 60), TwoHours).FirstConflict.ShouldBe(P(54, 55));
        calculator.Check(P(55, 60), TwoHours).IsAvailable.ShouldBeTrue();
    }

    [Fact]
    public void Without_any_vehicle_nothing_is_available()
    {
        var check = Calculator([], []).Check(P(0, 10), TwoHours);

        check.IsAvailable.ShouldBeFalse();
        check.FirstConflict.ShouldBe(P(0, 12));
        check.MinimumRemaining.ShouldBe(0);
        check.ConflictReason.ShouldBe(AvailabilityConflictReason.Capacity);
        Calculator([Vehicle(1)], []).Check(P(0, 10), TwoHours).IsAvailable.ShouldBeFalse();
    }

    [Fact]
    public void Only_the_first_conflicting_sub_period_is_reported_and_contiguous_conflicts_are_merged()
    {
        var calculator = Calculator(
            [AlwaysRentable(1)],
            [Booking(1, P(10, 20)), Booking(2, P(20, 25)), Booking(3, P(40, 50))]);

        var check = calculator.CheckOccupancy(P(0, 60));

        check.FirstConflict.ShouldBe(P(10, 25));
        check.MinimumRemaining.ShouldBe(0);
    }

    [Fact]
    public void The_minimum_remaining_capacity_tells_how_many_more_such_requests_fit()
    {
        var vehicles = Enumerable.Range(1, 4).Select(AlwaysRentable).ToArray();
        var calculator = Calculator(vehicles, [Booking(1, P(0, 10)), Booking(2, P(5, 15))]);

        calculator.CheckOccupancy(P(0, 20)).MinimumRemaining.ShouldBe(2);
        calculator.CheckOccupancy(P(15, 20)).MinimumRemaining.ShouldBe(4);
    }

    [Fact]
    public void A_category_already_over_capacity_reports_a_negative_remaining()
    {
        // Two bookings were accepted, then one of the two vehicles went to maintenance.
        var calculator = Calculator([AlwaysRentable(1)], [Booking(1, P(0, 10)), Booking(2, P(0, 10))]);

        calculator.CheckOccupancy(P(0, 10)).MinimumRemaining.ShouldBe(-1);
    }

    [Fact]
    public void The_profile_lists_capacity_and_occupancy_by_segment()
    {
        var calculator = Calculator(
            [AlwaysRentable(1), Vehicle(2, P(-1000, 20), P(26, 1000))],
            [Booking(1, P(0, 10)), Booking(2, P(5, 30))]);

        var profile = calculator.Profile(P(0, 40));

        profile.ShouldBe(
        [
            new CapacitySegment(P(0, 5), 2, 1),
            new CapacitySegment(P(5, 10), 2, 2),
            new CapacitySegment(P(10, 20), 2, 1),
            new CapacitySegment(P(20, 26), 1, 1),
            new CapacitySegment(P(26, 30), 2, 1),
            new CapacitySegment(P(30, 40), 2, 0),
        ]);
        profile.Select(segment => segment.Remaining).ShouldBe([1, 0, 1, 0, 1, 2]);
    }

    [Fact]
    public void The_profile_can_leave_out_a_booking_and_follows_the_channel_capacity()
    {
        var calculator = Calculator(
            [AlwaysRentable(1), AlwaysRentable(2)],
            [Booking(1, P(0, 10)), Booking(2, P(5, 30))],
            new ChannelRestrictions([], [P(25, 35)]));

        calculator.Profile(P(0, 40), BookingId(2)).ShouldBe(
        [
            new CapacitySegment(P(0, 10), 2, 1),
            new CapacitySegment(P(10, 25), 2, 0),
            new CapacitySegment(P(25, 35), 0, 0),
            new CapacitySegment(P(35, 40), 2, 0),
        ]);
    }

    [Fact]
    public void Both_constructors_compute_the_same_availability()
    {
        var vehicles = new[] { AlwaysRentable(1), Vehicle(2, P(0, 20)) };
        var occupancy = new[] { Booking(1, P(0, 10)), Booking(2, P(5, 30)) };
        var restrictions = new ChannelRestrictions([new ReservedCapacity(P(0, 8), 1)], []);

        var fromParts = new AvailabilityCalculator(CapacityTimeline.Fleet(vehicles), OccupancyTimeline.Of(occupancy), restrictions);
        var fromInputs = new AvailabilityCalculator(vehicles, occupancy, restrictions);

        fromParts.Profile(P(-5, 40)).ShouldBe(fromInputs.Profile(P(-5, 40)));
        fromParts.Restrictions.ShouldBeSameAs(restrictions);
        fromInputs.Occupancy.Intervals.Count.ShouldBe(2);
        new AvailabilityCalculator(vehicles, occupancy).Restrictions.ShouldBeSameAs(ChannelRestrictions.None);
    }

    [Fact]
    public void Null_arguments_are_bugs()
    {
        var calculator = Calculator([AlwaysRentable(1)], []);

        Should.Throw<ArgumentNullException>(() => calculator.Check(null!, TwoHours));
        Should.Throw<ArgumentNullException>(() => calculator.CheckOccupancy(null!));
        Should.Throw<ArgumentNullException>(() => calculator.Profile(null!));
        Should.Throw<ArgumentNullException>(() => new AvailabilityCalculator(null!, OccupancyTimeline.Empty, ChannelRestrictions.None));
        Should.Throw<ArgumentNullException>(() => new AvailabilityCalculator(StepFunction.Zero, null!, ChannelRestrictions.None));
        Should.Throw<ArgumentNullException>(() => new AvailabilityCalculator(StepFunction.Zero, OccupancyTimeline.Empty, null!));
        Should.Throw<ArgumentOutOfRangeException>(() => calculator.Check(P(0, 1), TimeSpan.FromMinutes(-1)));
    }
}
