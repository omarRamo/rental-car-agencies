namespace CarRental.Web.Components.Admin.Settings;

/// <summary>Unit in which a duration setting is edited.</summary>
public enum DurationUnit
{
    Minutes = 0,
    Hours = 1,
    Days = 2,
}

/// <summary>Converts a duration to an amount of its largest exact unit, and back.</summary>
public static class DurationUnits
{
    /// <summary>The largest unit that divides <paramref name="duration"/> exactly: 2 h gives (2, hours), 36 h (36, hours), 59 min (59, minutes).</summary>
    public static (int Amount, DurationUnit Unit) Split(TimeSpan duration)
    {
        var minutes = (long)duration.TotalMinutes;
        if (minutes != 0 && minutes % (24 * 60) == 0)
        {
            return ((int)(minutes / (24 * 60)), DurationUnit.Days);
        }

        return minutes != 0 && minutes % 60 == 0
            ? ((int)(minutes / 60), DurationUnit.Hours)
            : ((int)minutes, DurationUnit.Minutes);
    }

    public static TimeSpan Combine(int amount, DurationUnit unit) => unit switch
    {
        DurationUnit.Days => TimeSpan.FromDays(amount),
        DurationUnit.Hours => TimeSpan.FromHours(amount),
        _ => TimeSpan.FromMinutes(amount),
    };
}
