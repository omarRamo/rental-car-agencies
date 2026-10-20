using CarRental.Domain.Common;

namespace CarRental.Domain.Bookings.Availability;

/// <summary>
/// Restrictions of one sales channel on the capacity of one category (SPEC §7 "quotas et arrêt des ventes"): vehicles
/// reserved for the other channels (e.g. keep 2 vehicles out of Booking.com in high season) and stop-sell periods.
/// The capacity seen by the channel is <c>max(0, fleet − reserved)</c>, and 0 during a stop-sell.
/// </summary>
public sealed class ChannelRestrictions
{
    public ChannelRestrictions(IEnumerable<ReservedCapacity> reservedForOtherChannels, IEnumerable<Period> stopSales)
    {
        ArgumentNullException.ThrowIfNull(reservedForOtherChannels);
        ArgumentNullException.ThrowIfNull(stopSales);

        ReservedForOtherChannels =
        [
            .. reservedForOtherChannels.Select(reserved =>
                reserved ?? throw new ArgumentNullException(nameof(reservedForOtherChannels), "A reservation cannot be null.")),
        ];
        StopSales =
        [
            .. stopSales.Select(period => period ?? throw new ArgumentNullException(nameof(stopSales), "A stop-sell period cannot be null.")),
        ];
    }

    /// <summary>No restriction: the channel sees the whole fleet.</summary>
    public static ChannelRestrictions None { get; } = new([], []);

    /// <summary>Vehicles kept for the other channels; overlapping reservations add up.</summary>
    public IReadOnlyList<ReservedCapacity> ReservedForOtherChannels { get; }

    /// <summary>Periods during which the channel cannot sell the category at all.</summary>
    public IReadOnlyList<Period> StopSales { get; }

    /// <summary><see langword="true"/> when the restrictions remove nothing.</summary>
    public bool IsEmpty => StopSales.Count == 0 && ReservedForOtherChannels.All(reserved => reserved.Vehicles == 0);
}

/// <summary>Number of vehicles of the category reserved for the other channels during a period.</summary>
public sealed record ReservedCapacity
{
    public ReservedCapacity(Period period, int vehicles)
    {
        ArgumentNullException.ThrowIfNull(period);
        ArgumentOutOfRangeException.ThrowIfNegative(vehicles);
        Period = period;
        Vehicles = vehicles;
    }

    public Period Period { get; }

    public int Vehicles { get; }
}
