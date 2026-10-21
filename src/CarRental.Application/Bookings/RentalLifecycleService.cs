using CarRental.Application.Bookings.Availability;
using CarRental.Application.Bookings.Parties;
using CarRental.Application.Bookings.Writing;
using CarRental.Application.Customers.Contracts;
using CarRental.Application.Identity.Contracts;
using CarRental.Domain.Bookings;
using CarRental.Domain.Bookings.Availability;
using CarRental.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace CarRental.Application.Bookings;

/// <summary>
/// What a hand-over step of the departure and return package (1F-b) receives: the rental file right after its
/// transition (tracked), the instant and the actor, inside the unit of work of the transition.
/// </summary>
public sealed record HandoverContext(Booking Booking, DateTimeOffset Now, BookingActor Actor);

/// <summary>
/// Hand-over transitions of a rental file — departure (<c>StartRental</c>), return (<c>CompleteReturn</c>) and closing
/// (<c>Close</c>) — kept minimal: transition, timeline, outbox and availability cache. The departure and return package
/// (1F-b) plugs its own work (inspection, photos, signature, contract, return fees, deposit) through the optional
/// <c>alongside</c> step, run in the same unit of work right after the transition: if it fails, nothing is saved.
/// </summary>
/// <remarks>
/// Module-internal API: other modules never call it (they only see <c>Bookings.Contracts</c>); the screens of the
/// Bookings module do, through <c>IOperationRunner</c>.
/// </remarks>
public sealed class RentalLifecycleService
{
    private readonly BookingWriter _writer;
    private readonly DriverChecks _checks;
    private readonly ICustomerRegistration _registration;

    internal RentalLifecycleService(BookingWriter writer, DriverChecks checks, ICustomerRegistration registration)
    {
        _writer = writer;
        _checks = checks;
        _registration = registration;
    }

    /// <summary>
    /// Departure: <c>Confirmed</c> → <c>InProgress</c> at <paramref name="actualPickupAt"/> (now by default). The drivers
    /// must pass the customer check (generic error) and be eligible until the return; the assigned vehicle is checked
    /// under the lock (and the capacity too when the vehicle leaves before the planned pick-up).
    /// </summary>
    public async Task<Result> StartRentalAsync(
        Guid bookingId,
        DateTimeOffset? actualPickupAt = null,
        Func<HandoverContext, CancellationToken, Task<Result>>? alongside = null,
        CancellationToken cancellationToken = default)
    {
        if (!_writer.User.HasPermission(Permissions.HandoversManage))
        {
            return BookingErrors.Forbidden;
        }

        var booking = await _writer.Db.Set<Booking>().AsNoTracking().FirstOrDefaultAsync(b => b.Id == bookingId, cancellationToken);
        if (booking is null)
        {
            return BookingErrors.NotFound;
        }

        if (booking.Status == BookingStatus.Confirmed)
        {
            var checkedDrivers = await CheckDriversAsync(booking, actualPickupAt ?? _writer.Now(), cancellationToken);
            if (checkedDrivers.IsFailure)
            {
                return checkedDrivers;
            }
        }

        return await _writer.TransitionAsync(
            bookingId,
            async (tracked, now, ct) =>
            {
                var pickedUpAt = actualPickupAt ?? now;
                if (tracked.Status == BookingStatus.Confirmed && tracked.AssignedVehicleId is { } vehicleId && pickedUpAt <= now)
                {
                    var occupancy = new BookingOccupancy(tracked.Id, OccupancyKind.InProgress, tracked.Rental, pickedUpAt, assignedVehicleId: vehicleId);
                    var checkedVehicle = pickedUpAt < tracked.PickupAt
                        ? await _writer.Guard.EnsureAvailableAsync(
                            new AvailabilityRequest(tracked.CategoryId, tracked.Channel, occupancy)
                            {
                                IgnoreChannelRestrictions = true,
                                ComplianceWaived = tracked.IsComplianceWaived,
                            },
                            cancellationToken: ct)
                        : await _writer.Guard.EnsureVehicleAssignableAsync(
                            new VehicleAssignmentRequest(tracked.CategoryId, occupancy, vehicleId) { ComplianceWaived = tracked.IsComplianceWaived },
                            ct);
                    if (checkedVehicle.IsFailure)
                    {
                        return checkedVehicle;
                    }
                }

                var started = tracked.StartRental(pickedUpAt, _writer.Actor(), now);
                return started.IsFailure ? started : await AlongsideAsync(alongside, tracked, now, ct);
            },
            cancellationToken);
    }

    /// <summary>Return: <c>InProgress</c> → <c>Returned</c> at <paramref name="actualReturnAt"/> (now by default); releases the capacity after the buffer.</summary>
    public async Task<Result> CompleteReturnAsync(
        Guid bookingId,
        DateTimeOffset? actualReturnAt = null,
        Func<HandoverContext, CancellationToken, Task<Result>>? alongside = null,
        CancellationToken cancellationToken = default)
    {
        if (!_writer.User.HasPermission(Permissions.HandoversManage))
        {
            return BookingErrors.Forbidden;
        }

        return await _writer.TransitionAsync(
            bookingId,
            async (booking, now, ct) =>
            {
                var returned = booking.CompleteReturn(actualReturnAt ?? now, _writer.Actor(), now);
                return returned.IsFailure ? returned : await AlongsideAsync(alongside, booking, now, ct);
            },
            cancellationToken);
    }

    /// <summary>
    /// <c>Returned</c> → <c>Closed</c>, and the closing date given to the Customers module for each driver (retention of
    /// the document images). The balance and the deposit are checked by the caller (1F-b with the Payments module),
    /// typically in the <paramref name="alongside"/> step, which runs before the drivers are told.
    /// </summary>
    public async Task<Result> CloseAsync(
        Guid bookingId,
        Func<HandoverContext, CancellationToken, Task<Result>>? alongside = null,
        CancellationToken cancellationToken = default)
    {
        if (!_writer.User.HasPermission(Permissions.BookingsManage))
        {
            return BookingErrors.Forbidden;
        }

        return await _writer.TransitionAsync(
            bookingId,
            async (booking, now, ct) =>
            {
                var closed = booking.Close(_writer.Actor(), now);
                if (closed.IsFailure)
                {
                    return closed;
                }

                var step = await AlongsideAsync(alongside, booking, now, ct);
                if (step.IsFailure)
                {
                    return step;
                }

                foreach (var driverId in booking.Parties.Drivers)
                {
                    var recorded = await _registration.RecordRentalClosedAsync(driverId, now, ct);
                    if (recorded.IsFailure)
                    {
                        return recorded;
                    }
                }

                return Result.Success();
            },
            cancellationToken);
    }

    private Task<Result> AlongsideAsync(
        Func<HandoverContext, CancellationToken, Task<Result>>? alongside,
        Booking booking,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        alongside is null ? Task.FromResult(Result.Success()) : alongside(new HandoverContext(booking, now, _writer.Actor()), cancellationToken);

    /// <summary>The drivers pass the customer check (generic error) and are eligible from the departure to the return.</summary>
    private async Task<Result> CheckDriversAsync(Booking booking, DateTimeOffset departure, CancellationToken cancellationToken)
    {
        var drivers = booking.Parties.Drivers;
        if (await _checks.AnyFailsCustomerCheckAsync(drivers, cancellationToken))
        {
            return BookingErrors.CustomerCheckFailed;
        }

        var returnAt = booking.ReturnAt > departure ? booking.ReturnAt : departure.AddMinutes(1);
        var eligibility = await _checks.EligibilityAsync(drivers, departure, returnAt, booking.AgencyId, cancellationToken);
        if (eligibility.IsFailure)
        {
            return eligibility.Error;
        }

        return eligibility.Value.Any(report => !report.IsEligible) ? BookingErrors.DriversNotEligible : Result.Success();
    }
}
