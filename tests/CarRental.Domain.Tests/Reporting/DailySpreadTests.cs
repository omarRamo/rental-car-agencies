using System.Globalization;
using CarRental.Domain.Common;
using CarRental.Domain.Reporting;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;

namespace CarRental.Domain.Tests.Reporting;

/// <summary>The even spread of an amount over days (ADR-0045): truncated shares, remainder on the last day, exact sum.</summary>
public sealed class DailySpreadTests
{
    [Theory]
    [InlineData("100", 3, "33.333 33.333 33.334")]
    [InlineData("0.002", 3, "0 0 0.002")]
    [InlineData("350", 7, "50 50 50 50 50 50 50")]
    [InlineData("572.605", 5, "114.521 114.521 114.521 114.521 114.521")]
    [InlineData("1000", 6, "166.666 166.666 166.666 166.666 166.666 166.67")]
    [InlineData("10", 1, "10")]
    [InlineData("-10", 3, "-3.333 -3.333 -3.334")]
    [InlineData("0", 4, "0 0 0 0")]
    public void Every_day_gets_the_truncated_share_and_the_last_day_the_remainder(string total, int days, string expected)
    {
        var amount = Money.Tnd(decimal.Parse(total, CultureInfo.InvariantCulture));

        var shares = DailySpread.Shares(amount, days);

        shares.Select(s => s.Amount).ShouldBe(expected.Split(' ').Select(e => decimal.Parse(e, CultureInfo.InvariantCulture)));
        shares.Sum().ShouldBe(amount);
    }

    [Fact]
    public void A_single_share_is_the_same_as_in_the_whole_spread()
    {
        var total = Money.Tnd(1234.567m);
        var shares = DailySpread.Shares(total, 9);

        for (var i = 0; i < 9; i++)
        {
            DailySpread.ShareOf(total, 9, i).ShouldBe(shares[i]);
        }
    }

    [Fact]
    public void A_spread_needs_a_day_and_an_index_within_the_days()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => DailySpread.Shares(Money.Tnd(10m), 0));
        Should.Throw<ArgumentOutOfRangeException>(() => DailySpread.ShareOf(Money.Tnd(10m), 3, 3));
        Should.Throw<ArgumentOutOfRangeException>(() => DailySpread.ShareOf(Money.Tnd(10m), 3, -1));
    }

    [Property(MaxTest = 3000)]
    public Property The_shares_always_add_up_to_the_amount_and_differ_by_less_than_a_millime_per_day()
    {
        return Prop.ForAll(Amounts(), Gen.Choose(1, 400).ToArbitrary(), (amount, days) =>
        {
            var total = Money.Tnd(amount);
            var shares = DailySpread.Shares(total, days);
            var first = shares[0];
            var last = shares[^1];
            return shares.Count == days
                && shares.Sum() == total
                && shares.Take(days - 1).All(s => s == first)
                && Math.Abs(last.Amount - first.Amount) < days * 0.001m
                && shares.All(s => decimal.Round(s.Amount, 3) == s.Amount);
        });
    }

    /// <summary>Amounts in millimes between −1 000 000 and 1 000 000 TND.</summary>
    private static Arbitrary<decimal> Amounts() =>
        Gen.Choose(-1_000_000, 1_000_000)
            .SelectMany(dinars => Gen.Choose(0, 999).Select(millimes => dinars + (Math.Sign(dinars) * millimes / 1000m)))
            .ToArbitrary();
}
