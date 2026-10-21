using CarRental.Domain.Common;

namespace CarRental.Domain.Reporting;

/// <summary>
/// The rented days of a booking, as its quote prices them (ADR-0045): units of 24 hours from the planned pick-up, as
/// many as the days of the current quote; unit <c>i</c> starts at <c>pick-up + i × 24 h</c> and belongs to the Tunis
/// calendar date of that instant (the pricing engine dates its daily prices the same way). The return day is a rented day
/// only when the late-return tolerance made the quote count an extra unit.
/// </summary>
public static class RentalDays
{
    /// <summary>A rental unit: 24 hours from the pick-up time.</summary>
    public static readonly TimeSpan Unit = TimeSpan.FromDays(1);

    /// <summary>First instant (UTC) of unit <paramref name="index"/> (from 0).</summary>
    public static DateTimeOffset StartOf(DateTimeOffset pickupAt, int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        return pickupAt.ToUniversalTime() + (Unit * index);
    }

    /// <summary>The Tunis date unit <paramref name="index"/> is attributed to.</summary>
    public static DateOnly DateOf(DateTimeOffset pickupAt, int index) => TunisTime.DateOf(StartOf(pickupAt, index));

    /// <summary>The Tunis dates of the <paramref name="days"/> units of a rental, in order (one per unit).</summary>
    public static IReadOnlyList<DateOnly> DatesOf(DateTimeOffset pickupAt, int days)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(days, 1);
        var dates = new DateOnly[days];
        for (var i = 0; i < days; i++)
        {
            dates[i] = DateOf(pickupAt, i);
        }

        return dates;
    }
}
