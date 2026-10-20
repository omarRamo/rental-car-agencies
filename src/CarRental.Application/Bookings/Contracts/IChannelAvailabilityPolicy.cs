using CarRental.Domain.Common;

namespace CarRental.Application.Bookings.Contracts;

/// <summary>
/// Commercial restrictions of a sales channel on the capacity of a category (SPEC §7 "quotas et arrêt des ventes"),
/// read by the availability engine. Phase 1 registers a policy that restricts nothing; the Channels module replaces it
/// in phase 5 (register it with <c>services.Replace(...)</c> or after the Bookings module) and calls
/// <c>IAvailabilityChangeNotifier</c> when restrictions change.
/// </summary>
/// <remarks>
/// The capacity seen by the channel is <c>max(0, fleet − vehicles reserved for the other channels)</c>, and 0 during a
/// stop-sell; the occupancy always counts the bookings of every channel. Restrictions are commercial: they never
/// affect the physical capacity, and an agent may enter a booking already sold by a channel without them.
/// Implementations are called on every cache reload and every write check: keep them fast.
/// </remarks>
public interface IChannelAvailabilityPolicy
{
    Task<ChannelRestrictionsInfo> GetRestrictionsAsync(
        Guid categoryId,
        SalesChannel channel,
        Period range,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Restrictions of one channel for one category: vehicles kept for the other channels (overlapping reservations add
/// up) and periods during which the channel cannot sell the category.
/// </summary>
public sealed record ChannelRestrictionsInfo(IReadOnlyList<ReservedVehiclesInfo> ReservedForOtherChannels, IReadOnlyList<Period> StopSales)
{
    /// <summary>No restriction.</summary>
    public static ChannelRestrictionsInfo None { get; } = new([], []);
}

/// <summary>Number of vehicles reserved for the other channels during <c>Period</c> (e.g. keep 2 out of Booking.com in high season).</summary>
public sealed record ReservedVehiclesInfo(Period Period, int Vehicles);
