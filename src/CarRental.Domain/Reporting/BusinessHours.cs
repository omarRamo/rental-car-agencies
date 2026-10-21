using CarRental.Domain.Common;

namespace CarRental.Domain.Reporting;

/// <summary>
/// Time between two instants that falls within the opening hours of the agency (every Tunis day from
/// <c>opensAt</c> to <c>closesAt</c>): the "heures ouvrées" of the validation delay objective (SPEC §2, under 2 hours).
/// </summary>
public static class BusinessHours
{
    public static TimeSpan Between(DateTimeOffset start, DateTimeOffset end, TimeOnly opensAt, TimeOnly closesAt)
    {
        if (end <= start || closesAt <= opensAt)
        {
            return TimeSpan.Zero;
        }

        var total = TimeSpan.Zero;
        var last = TunisTime.DateOf(end);
        for (var day = TunisTime.DateOf(start); day <= last; day = day.AddDays(1))
        {
            var open = TunisTime.FromLocal(day, opensAt);
            var close = TunisTime.FromLocal(day, closesAt);
            var from = start > open ? start : open;
            var to = end < close ? end : close;
            if (to > from)
            {
                total += to - from;
            }
        }

        return total;
    }
}
