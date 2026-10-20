using CarRental.Domain.Bookings.Availability;
using CarRental.Domain.Common;
using static CarRental.Domain.Tests.Bookings.Availability.AvailabilityTestData;

namespace CarRental.Domain.Tests.Bookings.Availability;

/// <summary>Occupancy interval of a booking per docs/conception/phase-1-exploitation.md §3.</summary>
public sealed class OccupancyBasisTests
{
    // Planned rental: pick-up at +0 h, return at +48 h.
    private static readonly Period Rental = P(0, 48);

    private static OccupancyBasis BasisAt(double nowHours, double bufferHours = 2) =>
        new(At(nowHours), TimeSpan.FromHours(bufferHours));

    private static BookingOccupancy Booking(
        OccupancyKind kind,
        double? actualPickup = null,
        double? actualReturn = null,
        int? vehicle = null) =>
        new(
            BookingId(1),
            kind,
            Rental,
            actualPickup is { } pickup ? At(pickup) : null,
            actualReturn is { } back ? At(back) : null,
            vehicle is { } v ? VehicleId(v) : null);

    [Fact]
    public void A_planned_booking_occupies_from_pickup_to_return_plus_buffer()
    {
        BasisAt(-100).PeriodOf(Booking(OccupancyKind.Planned)).ShouldBe(P(0, 50));
    }

    [Fact]
    public void A_planned_booking_ignores_actual_times_and_the_current_instant()
    {
        BasisAt(30).PeriodOf(Booking(OccupancyKind.Planned, actualPickup: -1, actualReturn: 10)).ShouldBe(P(0, 50));
        BasisAt(500).PeriodOf(Booking(OccupancyKind.Planned)).ShouldBe(P(0, 50));
    }

    [Fact]
    public void A_zero_buffer_occupies_exactly_the_rental()
    {
        BasisAt(-1, bufferHours: 0).PeriodOf(Booking(OccupancyKind.Planned)).ShouldBe(Rental);
    }

    [Fact]
    public void A_rental_in_progress_on_time_occupies_from_the_actual_pickup_to_the_planned_return_plus_buffer()
    {
        BasisAt(10).PeriodOf(Booking(OccupancyKind.InProgress, actualPickup: -0.5)).ShouldBe(P(-0.5, 50));
    }

    [Fact]
    public void A_rental_in_progress_without_actual_pickup_uses_the_planned_pickup()
    {
        BasisAt(10).PeriodOf(Booking(OccupancyKind.InProgress)).ShouldBe(P(0, 50));
    }

    [Fact]
    public void An_overdue_rental_keeps_blocking_until_now_plus_buffer()
    {
        // Planned return at +48 h, still out at +53 h.
        BasisAt(53).PeriodOf(Booking(OccupancyKind.InProgress, actualPickup: 1)).ShouldBe(P(1, 55));
    }

    [Fact]
    public void An_overdue_rental_with_a_zero_buffer_is_still_out_at_the_current_instant()
    {
        var period = BasisAt(53, bufferHours: 0).PeriodOf(Booking(OccupancyKind.InProgress, actualPickup: 1))!;

        period.Start.ShouldBe(At(1));
        period.Contains(At(53)).ShouldBeTrue();
        period.End.ShouldBe(At(53).AddTicks(1));
    }

    [Fact]
    public void A_returned_rental_occupies_until_the_actual_return_plus_buffer()
    {
        // Early return frees capacity early; late return extends the occupancy.
        BasisAt(60).PeriodOf(Booking(OccupancyKind.Returned, actualPickup: 0.25, actualReturn: 30)).ShouldBe(P(0.25, 32));
        BasisAt(60).PeriodOf(Booking(OccupancyKind.Returned, actualPickup: 0.25, actualReturn: 51)).ShouldBe(P(0.25, 53));
        BasisAt(60).PeriodOf(Booking(OccupancyKind.Returned, actualReturn: 47)).ShouldBe(P(0, 49));
    }

    [Fact]
    public void A_returned_rental_without_buffer_returned_at_its_start_occupies_nothing()
    {
        BasisAt(60, bufferHours: 0).PeriodOf(Booking(OccupancyKind.Returned, actualPickup: 3, actualReturn: 3)).ShouldBeNull();
    }

    [Fact]
    public void Draft_and_terminal_bookings_occupy_nothing()
    {
        BasisAt(10).PeriodOf(Booking(OccupancyKind.None)).ShouldBeNull();
        BasisAt(10).IntervalOf(Booking(OccupancyKind.None)).ShouldBeNull();
    }

    [Fact]
    public void The_interval_carries_the_booking_and_its_assigned_vehicle()
    {
        var interval = BasisAt(-5).IntervalOf(Booking(OccupancyKind.Planned, vehicle: 7))!;

        interval.BookingId.ShouldBe(BookingId(1));
        interval.AssignedVehicleId.ShouldBe(VehicleId(7));
        interval.IsAssigned.ShouldBeTrue();
        interval.Period.ShouldBe(P(0, 50));
    }

    [Fact]
    public void The_occupancy_of_a_request_adds_the_buffer()
    {
        BasisAt(-5, bufferHours: 3).OccupancyOf(P(10, 20)).ShouldBe(P(10, 23));
    }

    [Fact]
    public void A_returned_booking_needs_its_actual_return()
    {
        Should.Throw<ArgumentException>(() => BasisAt(60).PeriodOf(Booking(OccupancyKind.Returned, actualPickup: 0)));
    }

    [Fact]
    public void An_actual_return_before_the_planned_pickup_without_actual_pickup_is_a_bug()
    {
        Should.Throw<ArgumentException>(() => BasisAt(60).PeriodOf(Booking(OccupancyKind.Returned, actualReturn: -3)));
    }

    [Fact]
    public void An_actual_pickup_after_the_end_of_an_in_progress_occupancy_is_a_bug()
    {
        // Picked up "in the future", after the planned return and after now + buffer.
        Should.Throw<ArgumentException>(() => BasisAt(10).PeriodOf(Booking(OccupancyKind.InProgress, actualPickup: 70)));
    }

    [Fact]
    public void The_basis_normalizes_the_current_instant_and_rejects_a_negative_buffer()
    {
        var basis = new OccupancyBasis(new DateTimeOffset(2027, 7, 1, 10, 0, 0, TimeSpan.FromHours(1)), TwoHours);

        basis.Now.Offset.ShouldBe(TimeSpan.Zero);
        basis.Now.ShouldBe(T0.AddHours(1));
        basis.PreparationBuffer.ShouldBe(TwoHours);
        Should.Throw<ArgumentOutOfRangeException>(() => new OccupancyBasis(T0, TimeSpan.FromMinutes(-1)));
    }

    [Fact]
    public void Booking_occupancy_data_is_validated()
    {
        Should.Throw<ArgumentException>(() => new BookingOccupancy(Guid.Empty, OccupancyKind.Planned, Rental));
        Should.Throw<ArgumentException>(() => new BookingOccupancy(BookingId(1), OccupancyKind.Planned, Rental, assignedVehicleId: Guid.Empty));
        Should.Throw<ArgumentNullException>(() => new BookingOccupancy(BookingId(1), OccupancyKind.Planned, null!));
        Should.Throw<ArgumentOutOfRangeException>(() => new BookingOccupancy(BookingId(1), (OccupancyKind)42, Rental));
        Should.Throw<ArgumentException>(() => new BookingOccupancy(BookingId(1), OccupancyKind.Returned, Rental, At(5), At(4)));
    }

    [Fact]
    public void Booking_occupancy_normalizes_actual_times_to_utc()
    {
        var local = new DateTimeOffset(2027, 7, 1, 10, 0, 0, TimeSpan.FromHours(1));

        var booking = new BookingOccupancy(BookingId(1), OccupancyKind.Returned, Rental, local, local.AddHours(3));

        booking.ActualPickupAt!.Value.Offset.ShouldBe(TimeSpan.Zero);
        booking.ActualReturnAt.ShouldBe(local.AddHours(3));
    }

    [Fact]
    public void Occupancy_intervals_are_validated()
    {
        Should.Throw<ArgumentException>(() => new OccupancyInterval(Guid.Empty, Rental));
        Should.Throw<ArgumentNullException>(() => new OccupancyInterval(BookingId(1), null!));
        Should.Throw<ArgumentException>(() => new OccupancyInterval(BookingId(1), Rental, Guid.Empty));
    }

    [Fact]
    public void An_interval_can_be_assigned_to_a_vehicle()
    {
        var interval = new OccupancyInterval(BookingId(1), Rental);

        interval.IsAssigned.ShouldBeFalse();
        interval.AssignedTo(VehicleId(3)).ShouldBe(new OccupancyInterval(BookingId(1), Rental, VehicleId(3)));
    }

    [Fact]
    public void Null_arguments_are_bugs()
    {
        Should.Throw<ArgumentNullException>(() => BasisAt(0).PeriodOf(null!));
        Should.Throw<ArgumentNullException>(() => BasisAt(0).IntervalOf(null!));
        Should.Throw<ArgumentNullException>(() => BasisAt(0).OccupancyOf(null!));
    }
}
