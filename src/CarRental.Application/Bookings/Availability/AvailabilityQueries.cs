using CarRental.Application.Bookings.Contracts;
using CarRental.Domain.Common;

namespace CarRental.Application.Bookings.Availability;

/// <summary>
/// Cached read side of the availability engine (<see cref="IAvailabilityQueries"/>): answers from the snapshot of each
/// category, and reads fresh data only for periods beyond the cached horizon.
/// </summary>
internal sealed class AvailabilityQueries(AvailabilityCache cache, TimeProvider timeProvider) : IAvailabilityQueries
{
    public async Task<IReadOnlyList<AvailableCategoryInfo>> GetAvailableCategoriesAsync(
        Guid agencyId,
        Period rental,
        SalesChannel channel,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rental);

        var categories = await cache.GetAgencyCategoriesAsync(agencyId, cancellationToken);
        var availabilities = await Task.WhenAll(
            categories.Select(category => GetCategoryAvailabilityAsync(category.Id, rental, channel, excludedBookingId: null, cancellationToken)));

        return
        [
            .. categories
                .Zip(availabilities)
                .Where(pair => pair.Second is { IsAvailable: true })
                .Select(pair => new AvailableCategoryInfo(pair.First, pair.Second!.Remaining)),
        ];
    }

    public async Task<CategoryAvailabilityInfo?> GetCategoryAvailabilityAsync(
        Guid categoryId,
        Period rental,
        SalesChannel channel,
        Guid? excludedBookingId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rental);

        var snapshot = await cache.GetSnapshotAsync(categoryId, cancellationToken);
        if (snapshot is null)
        {
            return null;
        }

        var occupancy = snapshot.Basis.OccupancyOf(rental);
        var window = AvailabilityWindows.FromNow(occupancy, timeProvider.GetUtcNow());
        if (window is null)
        {
            // Entirely in the past: nothing can be sold any more.
            return new CategoryAvailabilityInfo(categoryId, IsAvailable: false, Remaining: 0, FirstConflict: occupancy);
        }

        if (!snapshot.Covers(window))
        {
            snapshot = await cache.LoadUncachedAsync(categoryId, window, channel, cancellationToken);
            if (snapshot is null)
            {
                return null;
            }
        }

        var check = snapshot.CalculatorFor(channel).CheckOccupancy(window, excludedBookingId);
        return new CategoryAvailabilityInfo(categoryId, check.IsAvailable, Math.Max(0, check.MinimumRemaining), check.FirstConflict);
    }

    public async Task<IReadOnlyList<CapacitySegmentInfo>?> GetCapacityProfileAsync(
        Guid categoryId,
        Period range,
        SalesChannel? channel = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(range);

        var snapshot = await cache.GetSnapshotAsync(categoryId, cancellationToken);
        if (snapshot is null)
        {
            return null;
        }

        if (!snapshot.Covers(range))
        {
            snapshot = await cache.LoadUncachedAsync(categoryId, range, channel, cancellationToken);
            if (snapshot is null)
            {
                return null;
            }
        }

        return
        [
            .. snapshot.CalculatorFor(channel)
                .Profile(range)
                .Select(segment => new CapacitySegmentInfo(segment.Period, segment.Capacity, segment.Occupancy)),
        ];
    }
}
