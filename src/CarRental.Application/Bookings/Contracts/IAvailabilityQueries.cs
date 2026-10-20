using CarRental.Application.Fleet.Contracts;
using CarRental.Domain.Common;

namespace CarRental.Application.Bookings.Contracts;

/// <summary>
/// Read-only availability of the vehicle categories, for the public search, the WhatsApp bot, the channels and the
/// planning (docs/conception/phase-1-exploitation.md §3, ADR-0025). Served from an in-memory snapshot per category,
/// invalidated on every change, in well under 100 ms.
/// </summary>
/// <remarks>
/// An answer may be a few seconds old: never decide a write with it. A write re-checks under the availability lock
/// with fresh data (Bookings module), and the second of two concurrent requests for the last vehicle receives
/// <c>availability.not_available</c>. A rental is checked with its preparation buffer, <c>[start, end + buffer)</c>,
/// and only from the current instant on (the past is settled).
/// </remarks>
public interface IAvailabilityQueries
{
    /// <summary>
    /// The active categories of an agency that can still be sold for <paramref name="rental"/> on
    /// <paramref name="channel"/>, in display order, with the number of vehicles left.
    /// </summary>
    Task<IReadOnlyList<AvailableCategoryInfo>> GetAvailableCategoriesAsync(
        Guid agencyId,
        Period rental,
        SalesChannel channel,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether a category can still be sold for <paramref name="rental"/> on <paramref name="channel"/>;
    /// <paramref name="excludedBookingId"/> leaves out a booking being amended. <see langword="null"/> when the
    /// category does not exist.
    /// </summary>
    Task<CategoryAvailabilityInfo?> GetCategoryAvailabilityAsync(
        Guid categoryId,
        Period rental,
        SalesChannel channel,
        Guid? excludedBookingId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Capacity and occupancy of a category over <paramref name="range"/> (planning), as maximal segments covering it;
    /// the capacity is the whole fleet when <paramref name="channel"/> is <see langword="null"/>, otherwise what the
    /// channel may sell. <see langword="null"/> when the category does not exist.
    /// </summary>
    Task<IReadOnlyList<CapacitySegmentInfo>?> GetCapacityProfileAsync(
        Guid categoryId,
        Period range,
        SalesChannel? channel = null,
        CancellationToken cancellationToken = default);
}

/// <summary>A category that can be sold, and how many more bookings of the same period it can take.</summary>
public sealed record AvailableCategoryInfo(VehicleCategoryInfo Category, int Remaining);

/// <summary>
/// Availability of one category for one period: <c>Remaining</c> is how many more bookings of this period fit (0 when
/// not available); <c>FirstConflict</c> is the first sub-period without capacity (buffer included), or the whole
/// period when it lies in the past; <see langword="null"/> when available.
/// </summary>
public sealed record CategoryAvailabilityInfo(Guid CategoryId, bool IsAvailable, int Remaining, Period? FirstConflict);

/// <summary>A piece of the capacity profile of a category: vehicles that can be sold and bookings that occupy them.</summary>
public sealed record CapacitySegmentInfo(Period Period, int Capacity, int Occupancy)
{
    /// <summary>Bookings that still fit during the segment (negative when the category is over capacity).</summary>
    public int Remaining => Capacity - Occupancy;
}
