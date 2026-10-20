using CarRental.Domain.Bookings.Availability;
using CarRental.Domain.Common;

namespace CarRental.Application.Bookings.Availability;

/// <summary>
/// Port of the availability engine on the bookings of a category: their occupancy intervals over a range. Implemented
/// by the Bookings module from its booking data (internal to Bookings: other modules use <c>IAvailabilityQueries</c>).
/// </summary>
public interface IOccupancyReader
{
    /// <summary>The occupancy intervals (buffer included) of the bookings of a category that overlap a range.</summary>
    /// <param name="categoryId">The category whose bookings are read.</param>
    /// <param name="range">Only the intervals overlapping it are needed.</param>
    /// <param name="excludedBookingId">A booking to leave out (the one being amended or assigned).</param>
    /// <param name="basis">The current instant and preparation buffer to compute every interval with.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <remarks>
    /// <para>Implementation contract:</para>
    /// <list type="bullet">
    /// <item>Return every booking of <paramref name="categoryId"/> whose occupancy overlaps <paramref name="range"/>,
    /// except <paramref name="excludedBookingId"/>: map its status onto an <see cref="OccupancyKind"/> and compute its
    /// interval with <c>basis.IntervalOf(new BookingOccupancy(...))</c>, skipping a <see langword="null"/> result.
    /// Using <paramref name="basis"/> keeps the bookings and the request measured with the same instant and buffer.</item>
    /// <item>Read fresh data through the scoped <c>IAppDbContext</c> (inside the caller's transaction when there is
    /// one), never from a cache: the authoritative write check relies on it.</item>
    /// <item>Filter in SQL on a superset (pick-up before the range end, and planned or actual return after
    /// <c>range.Start − buffer</c>, or still <c>InProgress</c>), then compute and filter the exact intervals in memory.</item>
    /// </list>
    /// </remarks>
    Task<IReadOnlyList<OccupancyInterval>> GetOccupancyAsync(
        Guid categoryId,
        Period range,
        Guid? excludedBookingId,
        OccupancyBasis basis,
        CancellationToken cancellationToken = default);
}
