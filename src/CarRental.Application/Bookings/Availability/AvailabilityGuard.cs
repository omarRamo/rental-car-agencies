using CarRental.Application.Common.Concurrency;
using CarRental.Application.Common.Persistence;
using CarRental.Application.Fleet.Contracts;
using CarRental.Domain.Bookings.Availability;
using CarRental.Domain.Common;

namespace CarRental.Application.Bookings.Availability;

/// <inheritdoc cref="IAvailabilityGuard"/>
internal sealed class AvailabilityGuard(
    IUnitOfWork unitOfWork,
    IAvailabilityLock availabilityLock,
    AvailabilitySnapshotLoader loader,
    IFleetQueries fleet) : IAvailabilityGuard
{
    public async Task<Result> LockAsync(IEnumerable<Guid> categoryIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(categoryIds);
        if (!unitOfWork.HasActiveTransaction)
        {
            throw new InvalidOperationException(
                "The availability lock is owned by a transaction: call the availability guard inside IUnitOfWork.ExecuteAsync, then write in the same transaction.");
        }

        // A single global order (ascending ids) for every writer: two transactions never wait for each other in a cycle.
        var ordered = categoryIds.Distinct().Order().ToList();
        if (ordered.Contains(Guid.Empty))
        {
            throw new ArgumentException("A category id is required.", nameof(categoryIds));
        }

        foreach (var categoryId in ordered)
        {
            var acquired = await availabilityLock.AcquireAsync(categoryId, cancellationToken: cancellationToken);
            if (acquired.IsFailure)
            {
                return acquired;
            }
        }

        return Result.Success();
    }

    public async Task<Result> EnsureAvailableAsync(
        AvailabilityRequest request,
        Guid? previousCategoryId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        IEnumerable<Guid> categories = previousCategoryId is { } previous ? [request.CategoryId, previous] : [request.CategoryId];
        var locked = await LockAsync(categories, cancellationToken);
        if (locked.IsFailure)
        {
            return locked;
        }

        var context = await loader.LoadContextAsync(request.CategoryId, cancellationToken);
        if (context is null)
        {
            return AvailabilityErrors.CategoryNotFound;
        }

        var window = AvailabilityWindows.FromNow(context.Basis.PeriodOf(request.Booking), context.Basis.Now);
        if (window is null)
        {
            // The booking occupies nothing from now on: no capacity to protect.
            return Result.Success();
        }

        SalesChannel? channel = request.IgnoreChannelRestrictions ? null : request.Channel;
        IReadOnlyCollection<SalesChannel> channels = channel is { } sold ? [sold] : [];
        var snapshot = await loader.LoadSnapshotAsync(context, window, request.Booking.BookingId, channels, cancellationToken);
        return snapshot.CalculatorFor(channel).CheckOccupancy(window, request.Booking.BookingId).ToResult();
    }

    public async Task<Result> EnsureVehicleAssignableAsync(VehicleAssignmentRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var locked = await LockAsync([request.CategoryId], cancellationToken);
        if (locked.IsFailure)
        {
            return locked;
        }

        var context = await loader.LoadContextAsync(request.CategoryId, cancellationToken);
        if (context is null)
        {
            return AvailabilityErrors.CategoryNotFound;
        }

        var vehicle = await fleet.GetVehicleAsync(request.VehicleId, cancellationToken);
        if (vehicle is null || vehicle.CategoryId != request.CategoryId)
        {
            return AvailabilityErrors.VehicleNotInCategory;
        }

        if (vehicle.Status == VehicleStatusInfo.Sold)
        {
            return AvailabilityErrors.VehicleUnavailable;
        }

        var window = AvailabilityWindows.FromNow(context.Basis.PeriodOf(request.Booking), context.Basis.Now);
        if (window is null)
        {
            return Result.Success();
        }

        var vehicles = await loader.LoadVehiclesAsync(request.CategoryId, window, cancellationToken);
        var rentable = vehicles.FirstOrDefault(v => v.Id == request.VehicleId) ?? new RentableVehicle(vehicle.Id, vehicle.PlateNumber, []);
        var occupancy = await loader.LoadOccupancyAsync(context, window, request.Booking.BookingId, cancellationToken);
        return VehicleAssignmentRules.CanTake(
            rentable,
            vehicle.CompliantUntil,
            new OccupancyInterval(request.Booking.BookingId, window, request.VehicleId),
            request.Booking.Rental.End,
            occupancy,
            request.ComplianceWaived);
    }
}
