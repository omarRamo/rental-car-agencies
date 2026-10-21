using CarRental.Application.Referential.Contracts;

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
        if (minutes != 0 && minutes % MinutesPer(DurationUnit.Days) == 0)
        {
            return ((int)(minutes / MinutesPer(DurationUnit.Days)), DurationUnit.Days);
        }

        return minutes != 0 && minutes % MinutesPer(DurationUnit.Hours) == 0
            ? ((int)(minutes / MinutesPer(DurationUnit.Hours)), DurationUnit.Hours)
            : ((int)minutes, DurationUnit.Minutes);
    }

    /// <summary>
    /// The duration of <paramref name="amount"/> units; an amount beyond the longest duration setting gives
    /// <see cref="TimeSpan.MaxValue"/>, which the definition rejects (never an overflow).
    /// </summary>
    public static TimeSpan Combine(int amount, DurationUnit unit)
    {
        var minutes = (long)amount * MinutesPer(unit);
        return minutes > (long)SettingDefinitions.MaximumDuration.TotalMinutes ? TimeSpan.MaxValue : TimeSpan.FromMinutes(minutes);
    }

    /// <summary>The largest amount of <paramref name="unit"/> a duration setting can hold (365 days).</summary>
    public static int MaximumAmount(DurationUnit unit) => (int)((long)SettingDefinitions.MaximumDuration.TotalMinutes / MinutesPer(unit));

    private static long MinutesPer(DurationUnit unit) => unit switch
    {
        DurationUnit.Days => 24 * 60,
        DurationUnit.Hours => 60,
        _ => 1,
    };
}
