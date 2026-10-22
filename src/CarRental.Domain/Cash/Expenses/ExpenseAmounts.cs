using CarRental.Domain.Common;

namespace CarRental.Domain.Cash.Expenses;

/// <summary>
/// The amounts of an expense: excluding VAT (HT), VAT and VAT included (TTC), with the VAT rate when one applies. Entered
/// either from a rate and one amount — rounded like the pricing engine (HT = round(TTC ÷ (1 + rate)) to the millime,
/// VAT = TTC − HT; or VAT = round(HT × rate), TTC = HT + VAT) — or as printed on the supplier's invoice (HT and VAT).
/// Always HT + VAT = TTC.
/// </summary>
public sealed record ExpenseAmounts
{
    /// <summary>Upper bound of one expense, in TND (guard against typing errors).</summary>
    public const decimal MaxAmount = 1_000_000m;

    private ExpenseAmounts(Money exclVat, Money vat, Money inclVat, decimal? vatRate)
    {
        ExclVat = exclVat;
        Vat = vat;
        InclVat = inclVat;
        VatRate = vatRate;
    }

    public Money ExclVat { get; }

    public Money Vat { get; }

    public Money InclVat { get; }

    /// <summary>The rate entered, or the one the invoice amounts give (VAT ÷ HT, four decimals); none when the HT is zero.</summary>
    public decimal? VatRate { get; }

    /// <summary>From the amount VAT included and the rate: HT = round(TTC ÷ (1 + rate)), VAT = TTC − HT.</summary>
    public static Result<ExpenseAmounts> FromInclVat(Money inclVat, decimal rate)
    {
        if (!IsRate(rate))
        {
            return ExpenseErrors.VatRateInvalid;
        }

        if (!IsPositive(inclVat))
        {
            return ExpenseErrors.AmountInvalid;
        }

        var exclVat = Money.Tnd(inclVat.Amount / (1m + rate));
        return new ExpenseAmounts(exclVat, inclVat - exclVat, inclVat, rate);
    }

    /// <summary>From the amount excluding VAT and the rate: VAT = round(HT × rate), TTC = HT + VAT.</summary>
    public static Result<ExpenseAmounts> FromExclVat(Money exclVat, decimal rate)
    {
        if (!IsRate(rate))
        {
            return ExpenseErrors.VatRateInvalid;
        }

        if (!IsPositive(exclVat))
        {
            return ExpenseErrors.AmountInvalid;
        }

        var vat = exclVat.Multiply(rate);
        var inclVat = exclVat + vat;
        return inclVat.Amount > MaxAmount ? ExpenseErrors.AmountInvalid : new ExpenseAmounts(exclVat, vat, inclVat, rate);
    }

    /// <summary>As printed on the supplier's invoice: the amount excluding VAT and the VAT (TTC = HT + VAT).</summary>
    public static Result<ExpenseAmounts> FromInvoice(Money exclVat, Money vat)
    {
        if (exclVat.Currency != Currency.Tnd || vat.Currency != Currency.Tnd || exclVat.IsNegative || vat.IsNegative)
        {
            return ExpenseErrors.AmountInvalid;
        }

        var inclVat = exclVat + vat;
        if (!IsPositive(inclVat))
        {
            return ExpenseErrors.AmountInvalid;
        }

        decimal? rate = exclVat.IsPositive ? Math.Round(vat.Amount / exclVat.Amount, 4, MidpointRounding.AwayFromZero) : null;
        return rate is > 1m ? ExpenseErrors.VatRateInvalid : new ExpenseAmounts(exclVat, vat, inclVat, rate);
    }

    /// <summary>Amounts read back from storage (already checked when they were entered).</summary>
    public static ExpenseAmounts Restore(Money exclVat, Money vat, Money inclVat, decimal? vatRate) => new(exclVat, vat, inclVat, vatRate);

    private static bool IsRate(decimal rate) => rate is >= 0m and <= 1m;

    private static bool IsPositive(Money amount) => amount.Currency == Currency.Tnd && amount.IsPositive && amount.Amount <= MaxAmount;
}
