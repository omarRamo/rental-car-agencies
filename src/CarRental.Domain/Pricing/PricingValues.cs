using CarRental.Domain.Common;

namespace CarRental.Domain.Pricing;

/// <summary>Shared validation of the numbers held by rate plans.</summary>
internal static class PricingValues
{
    /// <summary>Upper bound of minimum durations and tier starts (one year).</summary>
    public const int MaximumMinimumDays = 365;

    public static bool IsMinimumDays(int days) => days is >= 0 and <= MaximumMinimumDays;

    /// <summary>A percentage with at most two decimals, within [0, 100] with the requested bounds.</summary>
    public static bool IsPercentage(decimal value, bool allowZero, bool allowHundred) =>
        decimal.Round(value, 2) == value
        && (allowZero ? value >= 0m : value > 0m)
        && (allowHundred ? value <= 100m : value < 100m);

    public static bool IsPositiveTnd(Money amount) => amount.Currency == Currency.Tnd && amount.IsPositive;

    public static bool IsNonNegativeTnd(Money amount) => amount.Currency == Currency.Tnd && !amount.IsNegative;
}
