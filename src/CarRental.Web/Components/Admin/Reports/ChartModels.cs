using System.Globalization;

namespace CarRental.Web.Components.Admin.Reports;

/// <summary>
/// A value of a chart: the category it belongs to (<paramref name="Label"/>, a shorter <paramref name="AxisLabel"/> under
/// the axis), the value (<see langword="null"/> when it cannot be computed: no bar) and its text as the tables show it.
/// </summary>
public sealed record ChartPoint(string Label, decimal? Value, string ValueText, string? AxisLabel = null);

/// <summary>A series of a grouped chart: its name (legend) and colour, and one point per group, in group order.</summary>
public sealed record ChartSeries(string Name, string Color, IReadOnlyList<ChartPoint> Points);

/// <summary>
/// The value axis of a chart: from zero to <see cref="Max"/>, with "nice" ticks (1, 2, 2.5 or 5 times a power of ten),
/// four or five of them; a fixed maximum (100 % for a rate) keeps its value.
/// </summary>
public sealed record ChartScale(decimal Max, decimal Step)
{
    public IReadOnlyList<decimal> Ticks
    {
        get
        {
            var ticks = new List<decimal>();
            for (var tick = 0m; tick <= Max; tick += Step)
            {
                ticks.Add(tick);
            }

            return ticks;
        }
    }

    /// <summary>The scale of values up to <paramref name="largest"/> (negative values are not drawn: bars grow from zero).</summary>
    public static ChartScale For(decimal largest, decimal? fixedMax = null)
    {
        if (fixedMax is { } max && max > 0m)
        {
            return new ChartScale(max, NiceStep(max / 4m));
        }

        if (largest <= 0m)
        {
            return new ChartScale(1m, 0.25m);
        }

        var step = NiceStep(largest / 4m);
        return new ChartScale(Math.Ceiling(largest / step) * step, step);
    }

    /// <summary>
    /// The scale of ratios up to <paramref name="largest"/>: from zero to a nice value just above it, 100 % at most (low
    /// rates stay readable; bars still start at zero).
    /// </summary>
    public static ChartScale ForRatio(decimal largest)
    {
        var scale = For(largest);
        return largest <= 0m || scale.Max >= 1m ? new ChartScale(1m, 0.25m) : scale;
    }

    /// <summary>Position of <paramref name="value"/> on the axis, in percent of its length (clamped to the axis).</summary>
    public decimal Percent(decimal value) => Math.Round(Math.Clamp(value, 0m, Max) / Max * 100m, 2);

    /// <summary>A percentage for an inline style (invariant: "42.5%").</summary>
    public string Css(decimal value) => Percent(value).ToString("0.##", CultureInfo.InvariantCulture) + "%";

    /// <summary>The share of the axis for a CSS multiplier (invariant: "0.425").</summary>
    public string Fraction(decimal value) => (Percent(value) / 100m).ToString("0.####", CultureInfo.InvariantCulture);

    private static decimal NiceStep(decimal rough)
    {
        var power = 1m;
        while (rough >= power * 10m)
        {
            power *= 10m;
        }

        while (rough < power && power > 0.0001m)
        {
            power /= 10m;
        }

        var unit = rough / power;
        var nice = unit <= 1m ? 1m : unit <= 2m ? 2m : unit <= 2.5m ? 2.5m : unit <= 5m ? 5m : 10m;
        return nice * power;
    }
}
