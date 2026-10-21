using CarRental.Domain.Common;

namespace CarRental.Domain.Reporting;

/// <summary>
/// Spreads an amount evenly over consecutive days, to the minor unit (the millime for TND), so that the shares always add
/// up to the amount exactly (ADR-0045): every day gets the amount divided by the number of days, truncated toward zero
/// to the minor unit, and the last day also takes the remainder. The same rule spreads the rental revenue of a booking,
/// its commission and the costs of a vehicle that cover a period.
/// </summary>
public static class DailySpread
{
    /// <summary>The share of each of <paramref name="days"/> days, in order; the last one carries the rounding remainder.</summary>
    public static IReadOnlyList<Money> Shares(Money total, int days)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(days, 1);
        var share = EvenShare(total, days);
        var shares = new Money[days];
        for (var i = 0; i < days - 1; i++)
        {
            shares[i] = share;
        }

        shares[days - 1] = total - (share * (days - 1));
        return shares;
    }

    /// <summary>The share of day <paramref name="index"/> (from 0) among <paramref name="days"/>, without building the others.</summary>
    public static Money ShareOf(Money total, int days, int index)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(days, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, days);
        var share = EvenShare(total, days);
        return index < days - 1 ? share : total - (share * (days - 1));
    }

    /// <summary>The amount divided by the number of days, truncated toward zero to the minor unit.</summary>
    private static Money EvenShare(Money total, int days)
    {
        var scale = MinorUnitScale(total.Currency);
        return Money.Of(decimal.Truncate(total.Amount * scale / days) / scale, total.Currency);
    }

    private static decimal MinorUnitScale(Currency currency)
    {
        var scale = 1m;
        for (var i = 0; i < currency.MinorUnits; i++)
        {
            scale *= 10m;
        }

        return scale;
    }
}
