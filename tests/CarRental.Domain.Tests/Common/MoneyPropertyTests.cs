using CarRental.Domain.Common;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;

namespace CarRental.Domain.Tests.Common;

/// <summary>Algebraic properties of <see cref="Money"/>, checked on thousands of random amounts.</summary>
public sealed class MoneyPropertyTests
{
    [Property(MaxTest = 2000)]
    public Property Rounding_is_idempotent_and_never_moves_more_than_half_a_millime()
    {
        return Prop.ForAll(Amounts(), amount =>
        {
            var money = Money.Tnd(amount);
            return money == Money.Tnd(money.Amount)
                && Math.Abs(money.Amount - amount) <= 0.0005m
                && decimal.Round(money.Amount, 3) == money.Amount;
        });
    }

    [Property(MaxTest = 2000)]
    public Property Addition_is_commutative_and_associative()
    {
        return Prop.ForAll(Amounts(), Amounts(), Amounts(), (a, b, c) =>
        {
            var x = Money.Tnd(a);
            var y = Money.Tnd(b);
            var z = Money.Tnd(c);
            return x + y == y + x && (x + y) + z == x + (y + z);
        });
    }

    [Property(MaxTest = 2000)]
    public Property Subtraction_reverses_addition_and_zero_is_neutral()
    {
        return Prop.ForAll(Amounts(), Amounts(), (a, b) =>
        {
            var x = Money.Tnd(a);
            var y = Money.Tnd(b);
            return x + y - y == x && x + Money.Zero == x && x - x == Money.Zero;
        });
    }

    [Property(MaxTest = 2000)]
    public Property Multiplying_by_an_integer_equals_repeated_addition()
    {
        return Prop.ForAll(Amounts(), Gen.Choose(0, 60).ToArbitrary(), (a, days) =>
        {
            var price = Money.Tnd(a);
            var repeated = Enumerable.Repeat(price, days).Sum();
            return price * days == repeated;
        });
    }

    [Property(MaxTest = 2000)]
    public Property Rounding_to_the_dinar_produces_whole_dinars_within_half_a_dinar()
    {
        return Prop.ForAll(Amounts(), a =>
        {
            var money = Money.Tnd(a);
            var rounded = money.RoundToMajorUnit();
            return rounded.Amount == decimal.Truncate(rounded.Amount)
                && Math.Abs(rounded.Amount - money.Amount) <= 0.5m;
        });
    }

    /// <summary>Amounts between -100 000 and 100 000 TND with up to 6 decimals.</summary>
    private static Arbitrary<decimal> Amounts() =>
        Gen.Choose(-100_000_000, 100_000_000)
            .SelectMany(units => Gen.Choose(0, 999).Select(extra => (units * 1000m + extra) / 1_000_000m))
            .ToArbitrary();
}
