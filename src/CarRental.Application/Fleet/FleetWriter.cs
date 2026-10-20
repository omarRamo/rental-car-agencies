using CarRental.Application.Common.Availability;
using CarRental.Application.Common.Concurrency;
using CarRental.Application.Common.Persistence;
using CarRental.Application.Common.Security;
using CarRental.Domain.Common;
using CarRental.Domain.Fleet;
using Microsoft.EntityFrameworkCore;

namespace CarRental.Application.Fleet;

/// <summary>
/// The write pipeline of the fleet (ADR-0021): one unit of work per command that loads the aggregate inside the
/// (replayable) operation, applies the change, takes the availability lock of every category whose capacity changes
/// (both categories of a category change, in the order of their ids) before saving, and signals those categories to the
/// availability cache once committed. A concurrent edit of the same vehicle surfaces as <see cref="FleetErrors.ConcurrentUpdate"/>.
/// </summary>
/// <remarks>Public only because the public fleet services receive it by injection; its members are internal.</remarks>
public sealed class FleetWriter(
    IAppDbContext db,
    IUnitOfWork unitOfWork,
    IAvailabilityLock availabilityLock,
    IAvailabilityChangeNotifier notifier,
    IUserContext userContext,
    TimeProvider timeProvider)
{
    internal ChangeStamp Stamp() => new(timeProvider.GetUtcNow(), userContext.UserName);

    /// <summary>Loads a vehicle with everything its invariants need (history excluded) and applies <paramref name="change"/>.</summary>
    internal Task<Result<T>> ChangeVehicleAsync<T>(
        Guid vehicleId,
        Func<Vehicle, ChangeStamp, CancellationToken, Task<Result<T>>> change,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);
        return RunAsync(
            async (changed, ct) =>
            {
                var vehicle = await db.LoadVehicleAsync(vehicleId, VehicleParts.All, ct);
                if (vehicle is null)
                {
                    return Result.Failure<T>(FleetErrors.VehicleNotFound);
                }

                var outcome = await change(vehicle, Stamp(), ct);
                return outcome.IsFailure ? outcome : await LockAsync(vehicle, changed, outcome, ct);
            },
            cancellationToken);
    }

    internal Task<Result> ChangeVehicleAsync(Guid vehicleId, Func<Vehicle, ChangeStamp, Result> change, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);
        return ToResult(ChangeVehicleAsync(
            vehicleId,
            (vehicle, stamp, _) =>
            {
                var outcome = change(vehicle, stamp);
                return Task.FromResult(outcome.IsSuccess ? Result.Success(true) : Result.Failure<bool>(outcome.Error));
            },
            cancellationToken));
    }

    /// <summary>Adds a new vehicle built by <paramref name="create"/> (inside the unit of work).</summary>
    internal Task<Result<Guid>> AddVehicleAsync(Func<ChangeStamp, CancellationToken, Task<Result<Vehicle>>> create, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(create);
        return RunAsync(
            async (changed, ct) =>
            {
                var created = await create(Stamp(), ct);
                if (created.IsFailure)
                {
                    return Result.Failure<Guid>(created.Error);
                }

                db.Set<Vehicle>().Add(created.Value);
                return await LockAsync(created.Value, changed, Result.Success(created.Value.Id), ct);
            },
            cancellationToken);
    }

    /// <summary>Applies a change to a category; categories do not carry capacity, the cache is signalled when asked.</summary>
    internal Task<Result<T>> ChangeCategoryAsync<T>(
        Guid categoryId,
        Func<VehicleCategory, CancellationToken, Task<Result<T>>> change,
        bool signalAvailability,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);
        return RunAsync(
            async (changed, ct) =>
            {
                var category = await db.Set<VehicleCategory>().FirstOrDefaultAsync(c => c.Id == categoryId, ct);
                if (category is null)
                {
                    return Result.Failure<T>(FleetErrors.CategoryNotFound);
                }

                var outcome = await change(category, ct);
                if (outcome.IsSuccess && signalAvailability)
                {
                    changed.Add(category.Id);
                }

                return outcome;
            },
            cancellationToken);
    }

    /// <summary>
    /// Runs <paramref name="operation"/> in a unit of work; the categories it adds to the set are signalled to the
    /// availability cache after the commit.
    /// </summary>
    internal async Task<Result<T>> RunAsync<T>(
        Func<HashSet<Guid>, CancellationToken, Task<Result<T>>> operation,
        CancellationToken cancellationToken)
    {
        var changed = new HashSet<Guid>();
        Result<T> result;
        try
        {
            result = await unitOfWork.ExecuteAsync(
                ct =>
                {
                    // The operation may be replayed after a transient failure: start from scratch.
                    changed.Clear();
                    return operation(changed, ct);
                },
                cancellationToken: cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return FleetErrors.ConcurrentUpdate;
        }

        if (result.IsSuccess)
        {
            foreach (var categoryId in changed)
            {
                notifier.CategoryChanged(categoryId);
            }
        }

        return result;
    }

    private static async Task<Result> ToResult(Task<Result<bool>> operation)
    {
        var result = await operation;
        return result.IsSuccess ? Result.Success() : result.Error;
    }

    private static Error MapLockError(Error error) =>
        error.Kind is ErrorKind.Conflict or ErrorKind.Failure ? FleetErrors.AvailabilityBusy : error;

    /// <summary>Locks the categories whose capacity the pending events change, in a stable order, and remembers them.</summary>
    private async Task<Result<T>> LockAsync<T>(Vehicle vehicle, HashSet<Guid> changed, Result<T> outcome, CancellationToken cancellationToken)
    {
        foreach (var availabilityChanged in vehicle.DomainEvents.OfType<VehicleAvailabilityChanged>())
        {
            changed.Add(availabilityChanged.CategoryId);
            if (availabilityChanged.PreviousCategoryId is { } previous)
            {
                changed.Add(previous);
            }
        }

        foreach (var categoryId in changed.Order())
        {
            var locked = await availabilityLock.AcquireAsync(categoryId, cancellationToken: cancellationToken);
            if (locked.IsFailure)
            {
                return Result.Failure<T>(MapLockError(locked.Error));
            }
        }

        return outcome;
    }
}
