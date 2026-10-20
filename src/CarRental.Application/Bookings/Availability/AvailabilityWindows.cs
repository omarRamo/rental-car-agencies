using CarRental.Domain.Common;

namespace CarRental.Application.Bookings.Availability;

internal static class AvailabilityWindows
{
    /// <summary>
    /// The part of <paramref name="occupancy"/> from <paramref name="now"/> on, or <see langword="null"/> when it is
    /// over: capacity only matters from now on, and the past (e.g. the start of a rental in progress) is settled.
    /// </summary>
    public static Period? FromNow(Period? occupancy, DateTimeOffset now)
    {
        if (occupancy is null || occupancy.End <= now)
        {
            return null;
        }

        return occupancy.Start >= now ? occupancy : Period.Create(now, occupancy.End).Value;
    }
}
