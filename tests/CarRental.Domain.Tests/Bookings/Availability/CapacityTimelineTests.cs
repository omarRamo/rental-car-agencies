using CarRental.Domain.Bookings.Availability;
using CarRental.Domain.Common;
using static CarRental.Domain.Tests.Bookings.Availability.AvailabilityTestData;

namespace CarRental.Domain.Tests.Bookings.Availability;

/// <summary>Capacity of a category: vehicles whose windows contain t, minus quotas of other channels, 0 on stop-sell.</summary>
public sealed class CapacityTimelineTests
{
    [Fact]
    public void Fleet_capacity_counts_the_vehicles_whose_windows_contain_each_instant()
    {
        var capacity = CapacityTimeline.Fleet(
        [
            Vehicle(1, P(0, 100)),
            Vehicle(2, P(10, 50)),
            Vehicle(3),
        ]);

        capacity.ValueAt(At(-1)).ShouldBe(0);
        capacity.ValueAt(At(5)).ShouldBe(1);
        capacity.ValueAt(At(10)).ShouldBe(2);
        capacity.ValueAt(At(50)).ShouldBe(1);
        capacity.ValueAt(At(100)).ShouldBe(0);
    }

    [Fact]
    public void A_maintenance_block_removes_the_vehicle_during_the_block_only()
    {
        // Rentable until +20 h, in the garage [20 h, 26 h), rentable again afterwards.
        var capacity = CapacityTimeline.Fleet([Vehicle(1, P(0, 20), P(26, 100)), Vehicle(2, P(0, 100))]);

        capacity.Segments(P(0, 100)).ShouldBe(
        [
            new StepSegment(P(0, 20), 2),
            new StepSegment(P(20, 26), 1),
            new StepSegment(P(26, 100), 2),
        ]);
    }

    [Fact]
    public void Overlapping_or_touching_windows_of_one_vehicle_count_once()
    {
        var vehicle = Vehicle(1, P(30, 40), P(0, 10), P(5, 20), P(20, 25));

        vehicle.Windows.ShouldBe([P(0, 25), P(30, 40)]);
        CapacityTimeline.Fleet([vehicle]).Maximum(P(0, 40)).ShouldBe(1);
    }

    [Fact]
    public void A_vehicle_listed_twice_counts_once()
    {
        var first = new RentableVehicle(VehicleId(1), "123 TU 4567", [P(0, 10)]);
        var again = new RentableVehicle(VehicleId(1), "123 TU 4567", [P(5, 20)]);

        var capacity = CapacityTimeline.Fleet([first, again]);

        capacity.ShouldBe(StepFunction.CountOf([P(0, 20)]));
    }

    [Fact]
    public void A_vehicle_knows_whether_one_window_contains_a_period()
    {
        var vehicle = Vehicle(1, P(0, 20), P(26, 100));

        vehicle.WindowContaining(P(2, 20)).ShouldBe(P(0, 20));
        vehicle.WindowContaining(P(30, 40)).ShouldBe(P(26, 100));
        vehicle.WindowContaining(P(10, 30)).ShouldBeNull();
        vehicle.WindowContaining(P(-1, 5)).ShouldBeNull();
        vehicle.IsRentableFor(P(26, 100)).ShouldBeTrue();
        vehicle.IsRentableFor(P(99, 101)).ShouldBeFalse();
        Vehicle(2).IsRentableFor(P(0, 1)).ShouldBeFalse();
    }

    [Fact]
    public void Vehicles_are_validated()
    {
        Should.Throw<ArgumentException>(() => new RentableVehicle(Guid.Empty, "123 TU 4567", []));
        Should.Throw<ArgumentException>(() => new RentableVehicle(VehicleId(1), " ", []));
        Should.Throw<ArgumentNullException>(() => new RentableVehicle(VehicleId(1), "123 TU 4567", null!));
        Should.Throw<ArgumentNullException>(() => new RentableVehicle(VehicleId(1), "123 TU 4567", [null!]));
        Should.Throw<ArgumentNullException>(() => Vehicle(1).WindowContaining(null!));
    }

    [Fact]
    public void Without_restriction_the_channel_capacity_is_the_fleet_capacity()
    {
        var fleet = CapacityTimeline.Fleet([Vehicle(1, P(0, 10))]);

        CapacityTimeline.ForChannel(fleet, ChannelRestrictions.None).ShouldBeSameAs(fleet);
        ChannelRestrictions.None.IsEmpty.ShouldBeTrue();
        new ChannelRestrictions([new ReservedCapacity(P(0, 5), 0)], []).IsEmpty.ShouldBeTrue();
    }

    [Fact]
    public void Vehicles_reserved_for_other_channels_are_removed_and_capacity_never_goes_below_zero()
    {
        var fleet = CapacityTimeline.Fleet([AlwaysRentable(1), AlwaysRentable(2), AlwaysRentable(3)]);
        var restrictions = new ChannelRestrictions(
            [new ReservedCapacity(P(10, 30), 2), new ReservedCapacity(P(20, 40), 2)],
            []);

        var capacity = CapacityTimeline.ForChannel(fleet, restrictions);

        capacity.Segments(P(0, 50)).ShouldBe(
        [
            new StepSegment(P(0, 10), 3),
            new StepSegment(P(10, 20), 1),
            new StepSegment(P(20, 30), 0),
            new StepSegment(P(30, 40), 1),
            new StepSegment(P(40, 50), 3),
        ]);
    }

    [Fact]
    public void A_stop_sell_closes_the_channel()
    {
        var fleet = CapacityTimeline.Fleet([AlwaysRentable(1), AlwaysRentable(2)]);
        var restrictions = new ChannelRestrictions([new ReservedCapacity(P(0, 20), 1)], [P(5, 8), P(7, 9)]);

        var capacity = CapacityTimeline.ForChannel(fleet, restrictions);

        capacity.Segments(P(0, 25)).ShouldBe(
        [
            new StepSegment(P(0, 5), 1),
            new StepSegment(P(5, 9), 0),
            new StepSegment(P(9, 20), 1),
            new StepSegment(P(20, 25), 2),
        ]);
    }

    [Fact]
    public void Restrictions_are_validated()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => new ReservedCapacity(P(0, 1), -1));
        Should.Throw<ArgumentNullException>(() => new ReservedCapacity(null!, 1));
        Should.Throw<ArgumentNullException>(() => new ChannelRestrictions(null!, []));
        Should.Throw<ArgumentNullException>(() => new ChannelRestrictions([], null!));
        Should.Throw<ArgumentNullException>(() => new ChannelRestrictions([null!], []));
        Should.Throw<ArgumentNullException>(() => new ChannelRestrictions([], [null!]));
        Should.Throw<ArgumentNullException>(() => CapacityTimeline.Fleet(null!));
        Should.Throw<ArgumentNullException>(() => CapacityTimeline.ForChannel(StepFunction.Zero, null!));
    }

    [Fact]
    public void Occupancy_counts_intervals_and_isolates_each_booking()
    {
        var occupancy = OccupancyTimeline.Of(
        [
            Booking(1, P(0, 10)),
            Booking(2, P(5, 15), vehicle: 1),
            Booking(2, P(20, 22), vehicle: 1),
        ]);

        occupancy.Intervals.Count.ShouldBe(3);
        occupancy.Counts.ValueAt(At(6)).ShouldBe(2);
        occupancy.CountsOf(BookingId(2)).ShouldBe(StepFunction.CountOf([P(5, 15), P(20, 22)]));
        occupancy.CountsOf(BookingId(9)).ShouldBe(StepFunction.Zero);
        occupancy.Contains(BookingId(1)).ShouldBeTrue();
        occupancy.Contains(BookingId(9)).ShouldBeFalse();
        OccupancyTimeline.Empty.Counts.ShouldBe(StepFunction.Zero);
        Should.Throw<ArgumentNullException>(() => OccupancyTimeline.Of(null!));
        Should.Throw<ArgumentNullException>(() => OccupancyTimeline.Of([null!]));
    }

    [Fact]
    public void Restriction_lists_are_copied()
    {
        var reserved = new List<ReservedCapacity> { new(P(0, 5), 1) };
        var stops = new List<Period> { P(1, 2) };
        var restrictions = new ChannelRestrictions(reserved, stops);

        reserved.Clear();
        stops.Clear();

        restrictions.ReservedForOtherChannels.Count.ShouldBe(1);
        restrictions.StopSales.Count.ShouldBe(1);
        restrictions.IsEmpty.ShouldBeFalse();
    }
}
