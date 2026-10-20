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
        var capacity = snapshot.CalculatorFor(channel).CheckOccupancy(window, request.Booking.BookingId).ToResult();
        if (capacity.IsFailure || request.Booking.AssignedVehicleId is not { } vehicleId)
        {
            return capacity;
        }

        // The booking keeps (or is pinned to) a vehicle: that vehicle must carry the new occupancy too (invariants 1 and 3).
        var vehicle = await fleet.GetVehicleAsync(vehicleId, cancellationToken);
        return CheckVehicle(
            new VehicleCheck(request.CategoryId, vehicleId, request.Booking, request.ComplianceWaived),
            vehicle,
            context.Basis,
            window,
            snapshot.Vehicles,
            snapshot.Occupancy);
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

        var window = AvailabilityWindows.FromNow(context.Basis.PeriodOf(request.Booking), context.Basis.Now);
        if (window is null)
        {
            // Correcting the past: nothing to protect any more.
            return Result.Success();
        }

        var vehicles = await loader.LoadVehiclesAsync(request.CategoryId, window, cancellationToken);
        var occupancy = await loader.LoadOccupancyAsync(context, window, request.Booking.BookingId, cancellationToken);
        return CheckVehicle(
            new VehicleCheck(request.CategoryId, request.VehicleId, request.Booking, request.ComplianceWaived),
            vehicle,
            context.Basis,
            window,
            vehicles,
            occupancy);
    }

    /// <summary>Whether the vehicle can carry the booking over <paramref name="window"/> (the part of its occupancy from now on).</summary>
    private static Result CheckVehicle(
        VehicleCheck check,
        VehicleInfo? vehicle,
        OccupancyBasis basis,
        Period window,
        IReadOnlyList<RentableVehicle> vehicles,
        IReadOnlyList<OccupancyInterval> occupancy)
    {
        if (vehicle is null || vehicle.CategoryId != check.CategoryId)
        {
            return AvailabilityErrors.VehicleNotInCategory;
        }

        if (vehicle.Status == VehicleStatusInfo.Sold)
        {
            return AvailabilityErrors.VehicleUnavailable;
        }

        // Fleet publishes no window after the compliance end: with a waiver and nothing left to check against the
        // windows, a vehicle in the garage or out of service must not slip through.
        var windowsCheckable = vehicle.CompliantUntil is { } until && until > window.Start;
        if (check.ComplianceWaived && !windowsCheckable && vehicle.Status != VehicleStatusInfo.Active)
        {
            return AvailabilityErrors.VehicleUnavailable;
        }

        var rentable = vehicles.FirstOrDefault(v => v.Id == check.VehicleId) ?? new RentableVehicle(vehicle.Id, vehicle.PlateNumber, []);
        return VehicleAssignmentRules.CanTake(
            rentable,
            vehicle.CompliantUntil,
            new OccupancyInterval(check.Booking.BookingId, window, check.VehicleId),
            basis.VehicleBackAt(check.Booking),
            occupancy,
            check.ComplianceWaived);
    }

    private sealed record VehicleCheck(Guid CategoryId, Guid VehicleId, BookingOccupancy Booking, bool ComplianceWaived);
}
