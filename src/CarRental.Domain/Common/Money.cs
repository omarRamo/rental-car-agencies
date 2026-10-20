using System.Globalization;

namespace CarRental.Domain.Common;

/// <summary>
/// An amount of money in a currency, always rounded to the currency minor unit (the millime for TND)
/// with <see cref="MidpointRounding.AwayFromZero"/>. Decimal arithmetic only, never floating point.
/// </summary>
/// <remarks>
/// <para><c>default(Money)</c> is zero Tunisian dinars, so the struct can never hold an invalid currency.</para>
/// <para>Combining amounts of different currencies is a programming error and throws <see cref="InvalidOperationException"/>.</para>
/// <para>Persisted as <c>decimal(18,3)</c> (TND) by an EF Core convention.</para>
/// </remarks>
public readonly struct Money : IEquatable<Money>, IComparable<Money>, IFormattable
{
    private readonly Currency? _currency;

    private Money(decimal amount, Currency currency)
    {
        Amount = amount;
        _currency = currency;
    }

    /// <summary>Amount rounded to the minor unit of <see cref="Currency"/>.</summary>
    public decimal Amount { get; }

    public Currency Currency => _currency ?? Currency.Tnd;

    /// <summary>Zero Tunisian dinars.</summary>
    public static Money Zero => default;

    public bool IsZero => Amount == 0m;

    public bool IsPositive => Amount > 0m;

    public bool IsNegative => Amount < 0m;

    public static Money operator +(Money left, Money right) => left.Add(right);

    public static Money operator -(Money left, Money right) => left.Subtract(right);

    public static Money operator -(Money value) => value.Negate();

    public static Money operator *(Money left, decimal factor) => left.Multiply(factor);

    public static Money operator *(decimal factor, Money right) => right.Multiply(factor);

    public static bool operator ==(Money left, Money right) => left.Equals(right);

    public static bool operator !=(Money left, Money right) => !left.Equals(right);

    public static bool operator <(Money left, Money right) => left.CompareTo(right) < 0;

    public static bool operator <=(Money left, Money right) => left.CompareTo(right) <= 0;

    public static bool operator >(Money left, Money right) => left.CompareTo(right) > 0;

    public static bool operator >=(Money left, Money right) => left.CompareTo(right) >= 0;

    /// <summary>Creates an amount, rounding it to the minor unit of <paramref name="currency"/> (away from zero).</summary>
    public static Money Of(decimal amount, Currency currency)
    {
        ArgumentNullException.ThrowIfNull(currency);
        return new Money(RoundToMinorUnit(amount, currency), currency);
    }

    /// <summary>Creates an amount in Tunisian dinars, rounded to the millime.</summary>
    public static Money Tnd(decimal amount) => Of(amount, Currency.Tnd);

    /// <summary>Zero in the given currency.</summary>
    public static Money ZeroIn(Currency currency)
    {
        ArgumentNullException.ThrowIfNull(currency);
        return new Money(0m, currency);
    }

    /// <summary>
    /// Creates an amount only if it is already expressed in the minor unit (for user input):
    /// <c>1.2345</c> TND is rejected instead of being silently rounded.
    /// </summary>
    public static Result<Money> FromExact(decimal amount, Currency currency)
    {
        ArgumentNullException.ThrowIfNull(currency);
        return RoundToMinorUnit(amount, currency) == amount
            ? new Money(amount, currency)
            : MoneyErrors.TooManyDecimals;
    }

    public static Money Add(Money left, Money right) => left.Add(right);

    public static Money Subtract(Money left, Money right) => left.Subtract(right);

    public static Money Multiply(Money left, decimal factor) => left.Multiply(factor);

    public static Money Negate(Money value) => value.Negate();

    public Money Add(Money other)
    {
        EnsureSameCurrency(other);
        return new Money(Amount + other.Amount, Currency);
    }

    public Money Subtract(Money other)
    {
        EnsureSameCurrency(other);
        return new Money(Amount - other.Amount, Currency);
    }

    /// <summary>Multiplies by a factor (quantity, rate, percentage) and rounds to the minor unit.</summary>
    public Money Multiply(decimal factor) => Of(Amount * factor, Currency);

    public Money Negate() => new(-Amount, Currency);

    public Money Abs() => new(Math.Abs(Amount), Currency);

    /// <summary>
    /// Rounds to the major unit (the dinar for TND), used for public prices. Midpoints go away from zero by default.
    /// </summary>
    public Money RoundToMajorUnit(MidpointRounding mode = MidpointRounding.AwayFromZero) =>
        new(Math.Round(Amount, 0, mode), Currency);

    public int CompareTo(Money other)
    {
        EnsureSameCurrency(other);
        return Amount.CompareTo(other.Amount);
    }

    public bool Equals(Money other) => Amount == other.Amount && Currency.Equals(other.Currency);

    public override bool Equals(object? obj) => obj is Money other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Amount, Currency);

    /// <summary>Invariant representation with all minor digits, e.g. <c>681.400 TND</c>.</summary>
    public override string ToString() => ToString("F", CultureInfo.InvariantCulture);

    /// <summary>
    /// Formats the amount with the currency minor digits followed by the currency code.
    /// <paramref name="format"/> is a standard numeric format letter (<c>N</c> by default, <c>F</c> without grouping).
    /// </summary>
    public string ToString(string? format, IFormatProvider? formatProvider)
    {
        var letter = string.IsNullOrEmpty(format) ? "N" : format;
        var number = Amount.ToString(letter + Currency.MinorUnits.ToString(CultureInfo.InvariantCulture), formatProvider);
        return $"{number} {Currency.Code}";
    }

    private static decimal RoundToMinorUnit(decimal amount, Currency currency) =>
        Math.Round(amount, currency.MinorUnits, MidpointRounding.AwayFromZero);

    private void EnsureSameCurrency(Money other)
    {
        if (!Currency.Equals(other.Currency))
        {
            throw new InvalidOperationException(
                $"Cannot combine amounts in {Currency.Code} and {other.Currency.Code}.");
        }
    }
}
