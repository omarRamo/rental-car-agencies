using System.Globalization;
using CarRental.Domain.Common;

namespace CarRental.Domain.Tests.Common;

public sealed class MoneyTests
{
    [Theory]
    [InlineData("1.0005", "1.001")]
    [InlineData("1.0004", "1.000")]
    [InlineData("-1.0005", "-1.001")]
    [InlineData("-1.0004", "-1.000")]
    [InlineData("2.4995", "2.500")]
    [InlineData("0.0005", "0.001")]
    [InlineData("681.4", "681.400")]
    public void Tnd_rounds_to_the_millime_away_from_zero(string input, string expected)
    {
        var money = Money.Tnd(decimal.Parse(input, CultureInfo.InvariantCulture));

        money.Amount.ShouldBe(decimal.Parse(expected, CultureInfo.InvariantCulture));
        money.Currency.ShouldBe(Currency.Tnd);
    }

    [Fact]
    public void Euro_rounds_to_the_cent()
    {
        Money.Of(10.005m, Currency.Eur).Amount.ShouldBe(10.01m);
        Money.Of(10.004m, Currency.Eur).Amount.ShouldBe(10.00m);
    }

    [Fact]
    public void Default_money_is_zero_dinars()
    {
        var money = default(Money);

        money.ShouldBe(Money.Zero);
        money.Currency.ShouldBe(Currency.Tnd);
        money.IsZero.ShouldBeTrue();
        money.ShouldBe(Money.Tnd(0m));
    }

    [Fact]
    public void Addition_and_subtraction_keep_millime_precision()
    {
        var total = Money.Tnd(330m) + Money.Tnd(280m) - Money.Tnd(30.5m);

        total.ShouldBe(Money.Tnd(579.5m));
    }

    [Fact]
    public void Multiplication_rounds_the_result()
    {
        // 5 % discount of 610.000 = 30.500; one third of 100 = 33.333(3) -> 33.333
        (Money.Tnd(610m) * 0.05m).ShouldBe(Money.Tnd(30.5m));
        (Money.Tnd(100m) * (1m / 3m)).Amount.ShouldBe(33.333m);
        (3 * Money.Tnd(110m)).ShouldBe(Money.Tnd(330m));
    }

    [Fact]
    public void Reference_example_contribution_with_vat_is_11_900()
    {
        // 5 days x 2.000 TND excl. VAT, VAT 19 %.
        var contributionExclVat = Money.Tnd(2m) * 5;
        var contributionInclVat = contributionExclVat * 1.19m;

        contributionInclVat.ShouldBe(Money.Tnd(11.9m));
    }

    [Theory]
    [InlineData("680.5", "681")]
    [InlineData("680.499", "680")]
    [InlineData("680.4995", "681")] // the amount is first rounded to the millime (680.500)
    [InlineData("-680.5", "-681")]
    [InlineData("681.400", "681")]
    public void RoundToMajorUnit_rounds_to_the_dinar_away_from_zero(string input, string expected)
    {
        Money.Tnd(decimal.Parse(input, CultureInfo.InvariantCulture)).RoundToMajorUnit()
            .ShouldBe(Money.Tnd(decimal.Parse(expected, CultureInfo.InvariantCulture)));
    }

    [Fact]
    public void RoundToMajorUnit_accepts_another_midpoint_mode()
    {
        Money.Tnd(680.5m).RoundToMajorUnit(MidpointRounding.ToEven).ShouldBe(Money.Tnd(680m));
    }

    [Fact]
    public void Combining_different_currencies_is_a_bug()
    {
        var dinars = Money.Tnd(1m);
        var euros = Money.Of(1m, Currency.Eur);

        Should.Throw<InvalidOperationException>(() => dinars + euros);
        Should.Throw<InvalidOperationException>(() => dinars - euros);
        Should.Throw<InvalidOperationException>(() => dinars < euros);
        Should.Throw<InvalidOperationException>(() => dinars.CompareTo(euros));
    }

    [Fact]
    public void Equality_compares_amount_and_currency()
    {
        Money.Tnd(1m).ShouldBe(Money.Tnd(1.000m));
        Money.Tnd(1m).GetHashCode().ShouldBe(Money.Tnd(1.000m).GetHashCode());
        (Money.Tnd(1m) == Money.Of(1m, Currency.Eur)).ShouldBeFalse();
        (Money.Tnd(1m) != Money.Tnd(2m)).ShouldBeTrue();
        Money.Tnd(1m).Equals((object)Money.Tnd(1m)).ShouldBeTrue();
        Money.Tnd(1m).Equals("1 TND").ShouldBeFalse();
    }

    [Fact]
    public void Comparison_operators_follow_amounts()
    {
        var small = Money.Tnd(10m);
        var large = Money.Tnd(20m);

        (small < large).ShouldBeTrue();
        (small <= large).ShouldBeTrue();
        (large > small).ShouldBeTrue();
        (large >= small).ShouldBeTrue();
        (small <= Money.Tnd(10m)).ShouldBeTrue();
        small.CompareTo(large).ShouldBeNegative();
    }

    [Fact]
    public void Sign_helpers_and_negation()
    {
        var discount = -Money.Tnd(30.5m);

        discount.IsNegative.ShouldBeTrue();
        discount.IsPositive.ShouldBeFalse();
        discount.Abs().ShouldBe(Money.Tnd(30.5m));
        discount.Negate().IsPositive.ShouldBeTrue();
        Money.Negate(discount).ShouldBe(Money.Tnd(30.5m));
    }

    [Fact]
    public void Static_named_operations_match_operators()
    {
        Money.Add(Money.Tnd(1m), Money.Tnd(2m)).ShouldBe(Money.Tnd(3m));
        Money.Subtract(Money.Tnd(1m), Money.Tnd(2m)).ShouldBe(Money.Tnd(-1m));
        Money.Multiply(Money.Tnd(1.5m), 3).ShouldBe(Money.Tnd(4.5m));
    }

    [Fact]
    public void FromExact_rejects_amounts_finer_than_the_minor_unit()
    {
        Money.FromExact(1.234m, Currency.Tnd).Value.ShouldBe(Money.Tnd(1.234m));

        var result = Money.FromExact(1.2345m, Currency.Tnd);
        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldBe(MoneyErrors.TooManyDecimals);

        Money.FromExact(1.234m, Currency.Eur).IsFailure.ShouldBeTrue();
    }

    [Fact]
    public void ZeroIn_creates_zero_in_the_requested_currency()
    {
        var zero = Money.ZeroIn(Currency.Eur);

        zero.IsZero.ShouldBeTrue();
        zero.Currency.ShouldBe(Currency.Eur);
        zero.ShouldNotBe(Money.Zero);
    }

    [Fact]
    public void ToString_is_invariant_with_all_minor_digits()
    {
        Money.Tnd(681.4m).ToString().ShouldBe("681.400 TND");
        Money.Tnd(-30.5m).ToString().ShouldBe("-30.500 TND");
        Money.Of(10m, Currency.Eur).ToString().ShouldBe("10.00 EUR");
    }

    [Fact]
    public void ToString_formats_with_the_requested_culture()
    {
        var amount = Money.Tnd(1234.5m);

        amount.ToString("N", CultureInfo.GetCultureInfo("en")).ShouldBe("1,234.500 TND");
        var french = amount.ToString("N", CultureInfo.GetCultureInfo("fr"));
        french.ShouldEndWith(",500 TND");
        french.ShouldStartWith("1");
        amount.ToString(null, CultureInfo.InvariantCulture).ShouldBe("1,234.500 TND");
        amount.ToString("F", CultureInfo.InvariantCulture).ShouldBe("1234.500 TND");
    }

    [Fact]
    public void Sum_adds_amounts_and_defaults_to_zero_dinars()
    {
        new[] { Money.Tnd(1.1m), Money.Tnd(2.2m), Money.Tnd(3.3m) }.Sum().ShouldBe(Money.Tnd(6.6m));
        Array.Empty<Money>().Sum().ShouldBe(Money.Zero);
        Array.Empty<Money>().Sum(Currency.Eur).ShouldBe(Money.ZeroIn(Currency.Eur));
        Should.Throw<InvalidOperationException>(() => new[] { Money.Of(1m, Currency.Eur) }.Sum());
    }

    [Fact]
    public void Of_requires_a_currency()
    {
        Should.Throw<ArgumentNullException>(() => Money.Of(1m, null!));
    }
}
