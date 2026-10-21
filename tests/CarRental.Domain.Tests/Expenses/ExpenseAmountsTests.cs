using CarRental.Domain.Cash.Expenses;
using CarRental.Domain.Common;
using FsCheck;
using FsCheck.Fluent;

namespace CarRental.Domain.Tests.Expenses;

/// <summary>The VAT split of the expenses, rounded like the pricing engine (HT = round(TTC ÷ (1 + rate)), VAT = TTC − HT).</summary>
public sealed class ExpenseAmountsTests
{
    [Theory]
    [InlineData(119, 0.19, 100, 19)]
    [InlineData(50, 0.19, 42.017, 7.983)]
    [InlineData(10.7, 0.07, 10, 0.7)]
    [InlineData(1, 0.13, 0.885, 0.115)]
    [InlineData(80, 0, 80, 0)]
    public void From_the_amount_vat_included_and_a_rate(decimal inclVat, decimal rate, decimal exclVat, decimal vat)
    {
        var amounts = ExpenseAmounts.FromInclVat(Money.Tnd(inclVat), rate).Value;

        amounts.ExclVat.ShouldBe(Money.Tnd(exclVat));
        amounts.Vat.ShouldBe(Money.Tnd(vat));
        amounts.InclVat.ShouldBe(Money.Tnd(inclVat));
        amounts.VatRate.ShouldBe(rate);
    }

    [Fact]
    public void From_the_amount_excluding_vat_and_a_rate()
    {
        var amounts = ExpenseAmounts.FromExclVat(Money.Tnd(42.017m), 0.19m).Value;

        amounts.Vat.ShouldBe(Money.Tnd(7.983m));
        amounts.InclVat.ShouldBe(Money.Tnd(50m));
    }

    [Fact]
    public void As_printed_on_the_invoice_the_rate_is_derived()
    {
        var amounts = ExpenseAmounts.FromInvoice(Money.Tnd(200m), Money.Tnd(38m)).Value;

        amounts.InclVat.ShouldBe(Money.Tnd(238m));
        amounts.VatRate.ShouldBe(0.19m);
        ExpenseAmounts.FromInvoice(Money.Zero, Money.Tnd(5m)).Value.VatRate.ShouldBeNull("no rate without an amount excluding VAT");
    }

    [Fact]
    public void Invalid_amounts_and_rates_are_refused()
    {
        ExpenseAmounts.FromInclVat(Money.Zero, 0.19m).Error.ShouldBe(ExpenseErrors.AmountInvalid);
        ExpenseAmounts.FromInclVat(Money.Tnd(-1m), 0.19m).Error.ShouldBe(ExpenseErrors.AmountInvalid);
        ExpenseAmounts.FromInclVat(Money.Tnd(1_000_000.001m), 0.19m).Error.ShouldBe(ExpenseErrors.AmountInvalid);
        ExpenseAmounts.FromInclVat(Money.Of(10m, Currency.Eur), 0.19m).Error.ShouldBe(ExpenseErrors.AmountInvalid);
        ExpenseAmounts.FromInclVat(Money.Tnd(10m), 1.5m).Error.ShouldBe(ExpenseErrors.VatRateInvalid);
        ExpenseAmounts.FromInclVat(Money.Tnd(10m), -0.01m).Error.ShouldBe(ExpenseErrors.VatRateInvalid);
        ExpenseAmounts.FromExclVat(Money.Tnd(999_999m), 0.19m).Error.ShouldBe(ExpenseErrors.AmountInvalid);
        ExpenseAmounts.FromInvoice(Money.Tnd(-1m), Money.Tnd(1m)).Error.ShouldBe(ExpenseErrors.AmountInvalid);
        ExpenseAmounts.FromInvoice(Money.Zero, Money.Zero).Error.ShouldBe(ExpenseErrors.AmountInvalid);
        ExpenseAmounts.FromInvoice(Money.Tnd(10m), Money.Tnd(11m)).Error.ShouldBe(ExpenseErrors.VatRateInvalid);
    }

    [FsCheck.Xunit.Property(MaxTest = 500)]
    public Property The_amount_excluding_vat_plus_the_vat_is_always_the_amount_vat_included()
    {
        var inputs =
            from millimes in Gen.Choose(1, 50_000_000)
            from rate in Gen.Elements(0m, 0.07m, 0.13m, 0.19m)
            select (Money.Tnd(millimes / 1000m), rate);

        return Prop.ForAll(inputs.ToArbitrary(), input =>
        {
            var (amount, rate) = input;
            var fromInclVat = ExpenseAmounts.FromInclVat(amount, rate).Value;
            var fromExclVat = ExpenseAmounts.FromExclVat(amount, rate).Value;
            return fromInclVat.ExclVat + fromInclVat.Vat == fromInclVat.InclVat
                && fromInclVat.ExclVat == Money.Tnd(amount.Amount / (1m + rate))
                && fromExclVat.ExclVat + fromExclVat.Vat == fromExclVat.InclVat
                && !fromInclVat.Vat.IsNegative;
        });
    }
}
