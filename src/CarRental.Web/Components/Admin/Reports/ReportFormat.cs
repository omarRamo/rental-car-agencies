using System.Globalization;
using CarRental.Domain.Common;
using CarRental.Domain.Reporting;

namespace CarRental.Web.Components.Admin.Reports;

/// <summary>
/// Display formats of the reports (current UI culture): amounts to the millime with their currency, ratios as
/// percentages, Tunis dates. A value that cannot be computed (zero denominator, ADR-0046) shows as <see cref="None"/>.
/// </summary>
public static class ReportFormat
{
    public const string None = "—";

    private static CultureInfo Culture => CultureInfo.CurrentCulture;

    public static string Amount(Money amount) => amount.ToString("N", Culture);

    public static string Amount(Money? amount) => amount is { } value ? Amount(value) : None;

    /// <summary>An amount to the millime without its currency, for the table cells (the column header names the currency).</summary>
    public static string Number(Money amount) =>
        amount.Amount.ToString("N" + amount.Currency.MinorUnits.ToString(CultureInfo.InvariantCulture), Culture);

    public static string Number(Money? amount) => amount is { } value ? Number(value) : None;

    /// <summary>A rate on an axis tick: whole percentages, one decimal for finer steps (2,5 %).</summary>
    public static string PercentTick(decimal value, decimal step) =>
        value.ToString(step * 100m % 1m == 0m ? "P0" : "P1", Culture);

    /// <summary>A ratio as a percentage with one decimal (0.4567 → 45,7 %).</summary>
    public static string Percent(decimal? ratio) => ratio is { } value ? value.ToString("P1", Culture) : None;

    public static string Count(int value) => value.ToString("N0", Culture);

    public static string Count(int? value) => value is { } count ? Count(count) : None;

    /// <summary>A day with its abbreviated month (21 oct. 2026).</summary>
    public static string Date(DateOnly day) => day.ToString("d MMM yyyy", Culture);

    /// <summary>A day of the current period, without its year (21 oct.).</summary>
    public static string ShortDate(DateOnly day) => day.ToString("d MMM", Culture);

    public static string Instant(DateTimeOffset instant) => TunisTime.ToTunis(instant).ToString("g", Culture);

    /// <summary>A month as a title (Octobre 2026).</summary>
    public static string Month(DateOnly anyDay)
    {
        var text = new DateOnly(anyDay.Year, anyDay.Month, 1).ToString("MMMM yyyy", Culture);
        return text.Length == 0 ? text : string.Concat(char.ToUpper(text[0], Culture).ToString(), text[1..]);
    }

    /// <summary>A waiting time: minutes under an hour (45 min), hours and minutes beyond (26 h 05 min, 2 h).</summary>
    public static string Duration(TimeSpan? duration)
    {
        if (duration is not { } value)
        {
            return None;
        }

        var minutes = (long)Math.Round(value.TotalMinutes, MidpointRounding.AwayFromZero);
        return minutes switch
        {
            < 60 => string.Create(Culture, $"{minutes} min"),
            _ when minutes % 60 == 0 => string.Create(Culture, $"{minutes / 60} h"),
            _ => string.Create(Culture, $"{minutes / 60} h {minutes % 60:00} min"),
        };
    }

    /// <summary>
    /// The value of an axis tick: integers from 1 up (compact from a thousand: 1,5 k), one or two decimals below.
    /// </summary>
    public static string Tick(decimal value, decimal step)
    {
        var magnitude = Math.Abs(value);
        if (magnitude >= 1_000_000m)
        {
            return (value / 1_000_000m).ToString("0.#", Culture) + " M";
        }

        if (magnitude >= 1_000m)
        {
            return (value / 1_000m).ToString("0.#", Culture) + " k";
        }

        return value.ToString(step >= 1m ? "N0" : step >= 0.1m ? "N1" : "N2", Culture);
    }

    /// <summary>
    /// The relative change of a value against the previous period (+12,3 %), or none when it cannot be computed; the
    /// direction is the one of the shown (rounded) change.
    /// </summary>
    public static ReportDelta? Change(decimal? current, decimal? previous)
    {
        if (KpiMath.Change(current, previous) is not { } change)
        {
            return null;
        }

        var shown = Math.Round(change, 3, MidpointRounding.AwayFromZero);
        return new ReportDelta(Math.Sign(shown), Signed(shown, Math.Abs(shown).ToString("P1", Culture)));
    }

    public static ReportDelta? Change(Money? current, Money? previous) => Change(current?.Amount, previous?.Amount);

    /// <summary>A relative change already computed (0.123 → +12,3 %), or <see cref="None"/>.</summary>
    public static string SignedPercent(decimal? change)
    {
        if (change is not { } value)
        {
            return None;
        }

        var shown = Math.Round(value, 3, MidpointRounding.AwayFromZero);
        return Signed(shown, Math.Abs(shown).ToString("P1", Culture));
    }

    public static ReportDelta? Change(int current, int previous) => Change((decimal)current, previous);

    /// <summary>
    /// The difference of two ratios in percentage points (45 % then 50 %: +5,0), formatted with <paramref name="pointsFormat"/>
    /// (its placeholder receives the signed number), or none when either ratio is missing.
    /// </summary>
    public static ReportDelta? Points(decimal? current, decimal? previous, string pointsFormat)
    {
        if (current is not { } now || previous is not { } before)
        {
            return null;
        }

        var points = Math.Round((now - before) * 100m, 1, MidpointRounding.AwayFromZero);
        return new ReportDelta(Math.Sign(points), string.Format(Culture, pointsFormat, Signed(points, Math.Abs(points).ToString("N1", Culture))));
    }

    /// <summary>The magnitude <paramref name="text"/> with the sign of <paramref name="value"/>: "+" or the culture's minus sign.</summary>
    private static string Signed(decimal value, string text) => value switch
    {
        > 0m => "+" + text,
        < 0m => Culture.NumberFormat.NegativeSign + text,
        _ => text,
    };
}

/// <summary>A change against the previous period: its direction (−1, 0, +1, shown by an arrow) and its signed text.</summary>
public sealed record ReportDelta(int Direction, string Text);
