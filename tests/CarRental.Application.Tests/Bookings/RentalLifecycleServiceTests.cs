using CarRental.Application.Customers.Contracts;
using CarRental.Application.Identity.Contracts;
using CarRental.Application.Tests.Bookings.Fakes;
using CarRental.Domain.Bookings;
using CarRental.Domain.Bookings.Availability;
using CarRental.Domain.Common;

namespace CarRental.Application.Tests.Bookings;

/// <summary>Departure, return and closing: the minimal transitions the departure and return package builds on.</summary>
public sealed class RentalLifecycleServiceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<(BookingsHarness Harness, Booking Booking)> AssignedAsync()
    {
        var harness = new BookingsHarness();
        var booking = await harness.ConfirmedAsync();
        (await harness.Assignments.AssignAsync(booking.Id, harness.VehicleA.Id, cancellationToken: Ct)).IsSuccess.ShouldBeTrue();
        harness.As(Roles.Preparer);
        harness.Reset();
        return (harness, booking);
    }

    [Fact]
    public async Task A_departure_checks_the_drivers_and_the_vehicle_then_starts_the_rental()
    {
        var (harness, booking) = await AssignedAsync();
        harness.Clock.SetUtcNow(harness.PickupAt.AddMinutes(10));

        (await harness.Lifecycle.StartRentalAsync(booking.Id, cancellationToken: Ct)).IsSuccess.ShouldBeTrue();

        booking.Status.ShouldBe(BookingStatus.InProgress);
        booking.ActualPickupAt.ShouldBe(harness.PickupAt.AddMinutes(10));
        var check = harness.Guard.AssignmentChecks.ShouldHaveSingleItem();
        check.VehicleId.ShouldBe(harness.VehicleA.Id);
        check.Booking.Kind.ShouldBe(OccupancyKind.InProgress);
        harness.Guard.AvailabilityChecks.ShouldBeEmpty("on time, the vehicle check is enough");
        harness.Customers.BlacklistChecks.ShouldBe([harness.MainDriverId]);
        harness.Notifier.Changed.ShouldBe([harness.Ecmr.Id]);
    }

    [Fact]
    public async Task An_early_departure_consumes_capacity_before_the_planned_pick_up_and_is_checked_as_such()
    {
        var (harness, booking) = await AssignedAsync();
        harness.Clock.SetUtcNow(harness.PickupAt.AddHours(-3));

        (await harness.Lifecycle.StartRentalAsync(booking.Id, cancellationToken: Ct)).IsSuccess.ShouldBeTrue();

        var (request, _) = harness.Guard.AvailabilityChecks.ShouldHaveSingleItem();
        request.Booking.ActualPickupAt.ShouldBe(harness.PickupAt.AddHours(-3));
        request.Booking.AssignedVehicleId.ShouldBe(harness.VehicleA.Id);
        request.IgnoreChannelRestrictions.ShouldBeTrue();
    }

    [Fact]
    public async Task A_driver_who_fails_a_check_cannot_leave()
    {
        var (harness, booking) = await AssignedAsync();
        harness.Clock.SetUtcNow(harness.PickupAt);
        harness.Customers.Issues[harness.MainDriverId] = [new EligibilityIssueInfo(EligibilityIssueKindInfo.DocumentExpiresBeforeReturn, IdentityDocumentTypeInfo.DrivingLicense, new DateOnly(2027, 6, 12), null, null)];

        (await harness.Lifecycle.StartRentalAsync(booking.Id, cancellationToken: Ct)).Error.ShouldBe(BookingErrors.DriversNotEligible);

        harness.Customers.Issues.Clear();
        harness.Customers.Blacklisted.Add(harness.MainDriverId);
        (await harness.Lifecycle.StartRentalAsync(booking.Id, cancellationToken: Ct)).Error.ShouldBe(BookingErrors.CustomerCheckFailed);
        booking.Status.ShouldBe(BookingStatus.Confirmed);
    }

    [Fact]
    public async Task The_step_of_the_departure_package_runs_in_the_same_unit_of_work()
    {
        var (harness, booking) = await AssignedAsync();
        harness.Clock.SetUtcNow(harness.PickupAt);
        var seen = BookingStatus.Draft;

        (await harness.Lifecycle.StartRentalAsync(
                booking.Id,
                alongside: (context, _) =>
                {
                    harness.UnitOfWork.HasActiveTransaction.ShouldBeTrue();
                    seen = context.Booking.Status;
                    return Task.FromResult(Result.Failure(Error.Validation("handover.inspection_incomplete", "Eight photos are required.")));
                },
                cancellationToken: Ct))
            .Error.Code.ShouldBe("handover.inspection_incomplete");

        seen.ShouldBe(BookingStatus.InProgress, "the step sees the file after its transition");
        harness.Journal.Entries.Skip(harness.JournalStart).ShouldContain("rollback");
        harness.Notifier.Changed.ShouldBeEmpty("nothing was committed");
    }

    [Fact]
    public async Task A_return_then_a_closing_tell_the_customers_module_for_each_driver()
    {
        var harness = new BookingsHarness();
        var booking = await harness.ConfirmedAsync(harness.Quick() with { SecondaryDriverId = harness.SecondDriverId });
        (await harness.Assignments.AssignAsync(booking.Id, harness.VehicleA.Id, cancellationToken: Ct)).IsSuccess.ShouldBeTrue();
        harness.Clock.SetUtcNow(harness.PickupAt);
        harness.As(Roles.Preparer);
        (await harness.Lifecycle.StartRentalAsync(booking.Id, cancellationToken: Ct)).IsSuccess.ShouldBeTrue();
        harness.Clock.SetUtcNow(harness.ReturnAt.AddMinutes(-20));

        (await harness.Lifecycle.CompleteReturnAsync(booking.Id, cancellationToken: Ct)).IsSuccess.ShouldBeTrue();
        booking.Status.ShouldBe(BookingStatus.Returned);
        booking.ActualReturnAt.ShouldBe(harness.ReturnAt.AddMinutes(-20));

        (await harness.Lifecycle.CloseAsync(booking.Id, cancellationToken: Ct)).Error.ShouldBe(BookingErrors.Forbidden, "closing settles the money: bookings.manage");
        harness.As(Roles.Agent);
        harness.Clock.Advance(TimeSpan.FromHours(1));
        (await harness.Lifecycle.CloseAsync(booking.Id, cancellationToken: Ct)).IsSuccess.ShouldBeTrue();

        booking.Status.ShouldBe(BookingStatus.Closed);
        harness.Customers.ClosedRentals.Select(c => c.PersonId).ShouldBe([harness.MainDriverId, harness.SecondDriverId]);
        harness.Customers.ClosedRentals.ShouldAllBe(c => c.ClosedAt == harness.ReturnAt.AddMinutes(40));
    }

    [Fact]
    public async Task Hand_overs_need_their_permission()
    {
        var (harness, booking) = await AssignedAsync();
        harness.As(Roles.Accountant);

        (await harness.Lifecycle.StartRentalAsync(booking.Id, cancellationToken: Ct)).Error.ShouldBe(BookingErrors.Forbidden);
        (await harness.Lifecycle.CompleteReturnAsync(booking.Id, cancellationToken: Ct)).Error.ShouldBe(BookingErrors.Forbidden);
    }
}
