using CarRental.Domain.Bookings.Availability;
using static CarRental.Domain.Tests.Bookings.Availability.AvailabilityTestData;

namespace CarRental.Domain.Tests.Bookings.Availability;

/// <summary>
/// A vehicle takes a booking only without overlap (buffer included, never waivable), with documents valid until the
/// return (waivable by the manager) and within its rentable windows (invariants 1 and 3).
/// </summary>
public sealed class VehicleAssignmentRulesTests
{
    // Rental [10 h, 20 h), occupancy [10 h, 22 h) with the 2 h buffer.
    private static readonly OccupancyInterval Candidate = Booking(1, P(10, 22), vehicle: 1);
    private static readonly DateTimeOffset ReturnAt = At(20);
    private static readonly DateTimeOffset CompliantForever = At(10_000);

    [Fact]
    public void A_free_compliant_vehicle_in_service_can_take_the_booking()
    {
        VehicleAssignmentRules.CanTake(AlwaysRentable(1), CompliantForever, Candidate, ReturnAt, []).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void An_overlap_with_another_booking_of_the_vehicle_is_refused_buffer_included()
    {
        // The other booking returns at 8 h: its buffer ends at 10 h, exactly at our pick-up.
        var touching = Booking(2, P(0, 10), vehicle: 1);
        var overlapping = Booking(3, P(0, 10.5), vehicle: 1);

        VehicleAssignmentRules.CanTake(AlwaysRentable(1), CompliantForever, Candidate, ReturnAt, [touching]).IsSuccess.ShouldBeTrue();
        VehicleAssignmentRules.CanTake(AlwaysRentable(1), CompliantForever, Candidate, ReturnAt, [touching, overlapping])
            .Error.ShouldBe(AvailabilityErrors.VehicleOverlap);
    }

    [Fact]
    public void Only_the_other_bookings_of_this_vehicle_count()
    {
        var sameBookingAgain = Booking(1, P(0, 30), vehicle: 1);
        var otherVehicle = Booking(2, P(10, 22), vehicle: 2);
        var unassigned = Booking(3, P(10, 22));

        VehicleAssignmentRules.CanTake(AlwaysRentable(1), CompliantForever, Candidate, ReturnAt, [sameBookingAgain, otherVehicle, unassigned])
            .IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void A_document_expiring_before_the_return_makes_the_vehicle_non_compliant()
    {
        VehicleAssignmentRules.CanTake(AlwaysRentable(1), At(19), Candidate, ReturnAt, []).Error.ShouldBe(AvailabilityErrors.VehicleNotCompliant);
        VehicleAssignmentRules.CanTake(AlwaysRentable(1), null, Candidate, ReturnAt, []).Error.ShouldBe(AvailabilityErrors.VehicleNotCompliant);
        VehicleAssignmentRules.IsCompliant(At(20), ReturnAt).ShouldBeTrue();
        VehicleAssignmentRules.IsCompliant(At(19.99), ReturnAt).ShouldBeFalse();
        VehicleAssignmentRules.IsCompliant(null, ReturnAt).ShouldBeFalse();
    }

    [Fact]
    public void Documents_valid_until_the_return_suffice_even_if_the_buffer_goes_beyond()
    {
        // Fleet cuts the window at the compliance end (20 h): the buffer after it is not a rental.
        var vehicle = Vehicle(1, P(-100, 20));

        VehicleAssignmentRules.CanTake(vehicle, At(20), Candidate, ReturnAt, []).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void A_manager_waiver_bypasses_compliance_but_never_an_overlap()
    {
        var vehicle = Vehicle(1, P(-100, 15));
        var overlapping = Booking(2, P(21, 30), vehicle: 1);

        VehicleAssignmentRules.CanTake(vehicle, At(15), Candidate, ReturnAt, [], complianceWaived: true).IsSuccess.ShouldBeTrue();
        VehicleAssignmentRules.CanTake(vehicle, null, Candidate, ReturnAt, [], complianceWaived: true).IsSuccess.ShouldBeTrue();
        VehicleAssignmentRules.CanTake(vehicle, At(15), Candidate, ReturnAt, [overlapping], complianceWaived: true)
            .Error.ShouldBe(AvailabilityErrors.VehicleOverlap);
    }

    [Fact]
    public void A_vehicle_out_of_service_during_the_occupancy_is_unavailable()
    {
        var inTheGarage = Vehicle(1, P(-100, 12), P(14, 100));
        var notYetInService = Vehicle(2, P(11, 100));

        VehicleAssignmentRules.CanTake(inTheGarage, CompliantForever, Candidate, ReturnAt, []).Error.ShouldBe(AvailabilityErrors.VehicleUnavailable);
        VehicleAssignmentRules.CanTake(notYetInService, CompliantForever, Candidate, ReturnAt, []).Error.ShouldBe(AvailabilityErrors.VehicleUnavailable);
        VehicleAssignmentRules.CanTake(Vehicle(3), CompliantForever, Candidate, ReturnAt, []).Error.ShouldBe(AvailabilityErrors.VehicleUnavailable);
    }

    [Fact]
    public void A_waiver_still_checks_the_windows_before_the_compliance_end()
    {
        // Maintenance [12 h, 14 h) before the insurance end at 15 h: the waiver does not hide it.
        var vehicle = Vehicle(1, P(-100, 12), P(14, 15));

        VehicleAssignmentRules.CanTake(vehicle, At(15), Candidate, ReturnAt, [], complianceWaived: true)
            .Error.ShouldBe(AvailabilityErrors.VehicleUnavailable);
    }

    [Fact]
    public void Overlap_is_reported_before_compliance_and_compliance_before_unavailability()
    {
        var vehicle = Vehicle(1, P(-100, 12));
        var overlapping = Booking(2, P(21, 30), vehicle: 1);

        VehicleAssignmentRules.CanTake(vehicle, At(11), Candidate, ReturnAt, [overlapping]).Error.ShouldBe(AvailabilityErrors.VehicleOverlap);
        VehicleAssignmentRules.CanTake(vehicle, At(11), Candidate, ReturnAt, []).Error.ShouldBe(AvailabilityErrors.VehicleNotCompliant);
    }

    [Fact]
    public void Null_arguments_are_bugs()
    {
        Should.Throw<ArgumentNullException>(() => VehicleAssignmentRules.CanTake(null!, CompliantForever, Candidate, ReturnAt, []));
        Should.Throw<ArgumentNullException>(() => VehicleAssignmentRules.CanTake(AlwaysRentable(1), CompliantForever, null!, ReturnAt, []));
        Should.Throw<ArgumentNullException>(() => VehicleAssignmentRules.CanTake(AlwaysRentable(1), CompliantForever, Candidate, ReturnAt, null!));
    }
}
