using CarRental.Domain.Common;

namespace CarRental.Domain.Reporting;

/// <summary>
/// The arithmetic of the indicators (ADR-0046): a ratio or an amount per unit has no value (<see langword="null"/>, shown
/// as "—") when its denominator is zero, never zero or infinity; amounts per unit are rounded to the millime.
/// </summary>
public static class KpiMath
{
    /// <summary><paramref name="numerator"/> ÷ <paramref name="denominator"/>, or <see langword="null"/> for a zero denominator.</summary>
    public static decimal? Ratio(decimal numerator, decimal denominator) => denominator == 0m ? null : numerator / denominator;

    /// <summary>An amount per unit (per day, per vehicle-day), rounded to the minor unit, or <see langword="null"/> for zero units.</summary>
    public static Money? PerUnit(Money amount, decimal units) => units == 0m ? null : Money.Of(amount.Amount / units, amount.Currency);

    /// <summary>
    /// Relative change from <paramref name="previous"/> to <paramref name="current"/>: (current − previous) ÷ |previous|,
    /// or <see langword="null"/> when either is missing or the previous value is zero.
    /// </summary>
    public static decimal? Change(decimal? current, decimal? previous) =>
        current is { } now && previous is { } before && before != 0m ? (now - before) / Math.Abs(before) : null;

    public static decimal? Change(Money? current, Money? previous) => Change(current?.Amount, previous?.Amount);

    /// <summary>The median of durations: the middle one, or the mean of the two middle ones; <see langword="null"/> when empty.</summary>
    public static TimeSpan? Median(IEnumerable<TimeSpan> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var sorted = values.Order().ToList();
        if (sorted.Count == 0)
        {
            return null;
        }

        var middle = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
    }
}
