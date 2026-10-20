namespace CarRental.Domain.Common;

public static class MoneyExtensions
{
    /// <summary>Sums amounts of the same currency; an empty sequence sums to zero in <paramref name="currency"/> (TND by default).</summary>
    public static Money Sum(this IEnumerable<Money> amounts, Currency? currency = null)
    {
        ArgumentNullException.ThrowIfNull(amounts);
        var total = Money.ZeroIn(currency ?? Currency.Tnd);
        foreach (var amount in amounts)
        {
            total += amount;
        }

        return total;
    }
}
