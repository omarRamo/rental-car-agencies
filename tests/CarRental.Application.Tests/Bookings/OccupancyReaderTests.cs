using CarRental.Application.Identity.Contracts;
using CarRental.Application.Tests.Bookings.Fakes;
using CarRental.Domain.Bookings.Availability;
using CarRental.Domain.Common;

namespace CarRental.Application.Tests.Bookings;

/// <summary>
/// The occupancy port of the availability engine on the rental files: statuses mapped onto occupancy kinds (ADR-0025),
/// intervals computed with the basis of the caller, the excluded file left out, only what overlaps the range.
/// </summary>
public sealed class OccupancyReaderTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly TimeSpan Buffer = TimeSpan.FromHours(2);

    [Fact]
    public async Task Only_files_that_occupy_capacity_are_read_with_their_buffer()
    {
        var harness = new BookingsHarness();
        var confirmed = await harness.ConfirmedAsync();
        var pending = await harness.PendingAsync();
        var cancelled = await harness.ConfirmedAsync();
        (await harness.Workflow.CancelAsync(cancelled.Id, "Annulation", cancellationToken: Ct)).IsSuccess.ShouldBeTrue();
        var draft = await harness.Requests.CreateDraftAsync(
            new(SalesChannel.Web, Price: new(harness.AgencyId, harness.Ecmr.Id, harness.OfficeId, harness.OfficeId, harness.PickupAt, harness.ReturnAt, SalesChannel.Web)),
            Ct);
        draft.IsSuccess.ShouldBeTrue();
        var basis = new OccupancyBasis(BookingsHarness.Start, Buffer);
        var range = Period.Create(harness.PickupAt.AddDays(-1), harness.ReturnAt.AddDays(1)).Value;

        var intervals = await harness.OccupancyReader.GetOccupancyAsync(harness.Ecmr.Id, range, excludedBookingId: null, basis, Ct);

        intervals.Select(i => i.BookingId).ShouldBe([confirmed.Id, pending.Id], ignoreOrder: true);
        intervals.ShouldAllBe(i => i.Period == Period.Create(harness.PickupAt, harness.ReturnAt + Buffer).Value);
        (await harness.OccupancyReader.GetOccupancyAsync(harness.Ecmr.Id, range, confirmed.Id, basis, Ct)).Select(i => i.BookingId).ShouldBe([pending.Id]);
        (await harness.OccupancyReader.GetOccupancyAsync(harness.Cdmr.Id, range, null, basis, Ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_late_rental_keeps_blocking_until_now_plus_the_buffer_and_a_returned_one_until_its_return_plus_the_buffer()
    {
        var harness = new BookingsHarness();
        var late = await harness.ConfirmedAsync();
        var returned = await harness.ConfirmedAsync();
        foreach (var (booking, vehicle) in new[] { (late, harness.VehicleA.Id), (returned, harness.VehicleB.Id) })
        {
            (await harness.Assignments.AssignAsync(booking.Id, vehicle, cancellationToken: Ct)).IsSuccess.ShouldBeTrue();
        }

        harness.As(Roles.Preparer);
        harness.Clock.SetUtcNow(harness.PickupAt);
        (await harness.Lifecycle.StartRentalAsync(late.Id, cancellationToken: Ct)).IsSuccess.ShouldBeTrue();
        (await harness.Lifecycle.StartRentalAsync(returned.Id, cancellationToken: Ct)).IsSuccess.ShouldBeTrue();
        harness.Clock.SetUtcNow(harness.ReturnAt.AddHours(-5));
        (await harness.Lifecycle.CompleteReturnAsync(returned.Id, cancellationToken: Ct)).IsSuccess.ShouldBeTrue();
        var now = harness.ReturnAt.AddHours(10);
        var basis = new OccupancyBasis(now, Buffer);
        var range = Period.Create(now, now.AddDays(5)).Value;

        var intervals = await harness.OccupancyReader.GetOccupancyAsync(harness.Ecmr.Id, range, null, basis, Ct);

        var overdue = intervals.ShouldHaveSingleItem("the returned file ended before the range");
        overdue.BookingId.ShouldBe(late.Id);
        overdue.Period.End.ShouldBe(now + Buffer);
        overdue.AssignedVehicleId.ShouldBe(harness.VehicleA.Id);

        var before = await harness.OccupancyReader.GetOccupancyAsync(
            harness.Ecmr.Id,
            Period.Create(harness.ReturnAt.AddHours(-6), harness.ReturnAt).Value,
            null,
            basis,
            Ct);
        before.Single(i => i.BookingId == returned.Id).Period.End.ShouldBe(harness.ReturnAt.AddHours(-5) + Buffer);
    }

    [Fact]
    public async Task A_guaranteed_model_occupies_its_vehicle()
    {
        var harness = new BookingsHarness();
        var booking = await harness.ConfirmedAsync(harness.Quick() with { GuaranteedVehicleId = harness.VehicleB.Id });

        var intervals = await harness.OccupancyReader.GetOccupancyAsync(
            harness.Ecmr.Id,
            Period.Create(harness.PickupAt, harness.ReturnAt).Value,
            null,
            new OccupancyBasis(BookingsHarness.Start, Buffer),
            Ct);

        intervals.ShouldHaveSingleItem().AssignedVehicleId.ShouldBe(harness.VehicleB.Id);
        booking.GuaranteedVehicleId.ShouldBe(harness.VehicleB.Id);
    }
}
