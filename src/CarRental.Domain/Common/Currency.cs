using System.Collections.Frozen;

namespace CarRental.Domain.Common;

/// <summary>
/// ISO 4217 currency with its number of minor units. The platform works in Tunisian dinars (3 decimals: millimes);
/// euro and dollar exist for indicative equivalents only.
/// </summary>
public sealed class Currency : IEquatable<Currency>
{
    public static readonly Currency Tnd = new("TND", 3);
    public static readonly Currency Eur = new("EUR", 2);
    public static readonly Currency Usd = new("USD", 2);

    private static readonly FrozenDictionary<string, Currency> KnownCurrencies =
        new[] { Tnd, Eur, Usd }.ToFrozenDictionary(c => c.Code, StringComparer.Ordinal);

    private Currency(string code, int minorUnits)
    {
        Code = code;
        MinorUnits = minorUnits;
    }

    /// <summary>ISO 4217 alphabetic code, upper case.</summary>
    public string Code { get; }

    /// <summary>Number of decimals of the minor unit (3 for TND, 2 for EUR).</summary>
    public int MinorUnits { get; }

    public static IReadOnlyCollection<Currency> All => KnownCurrencies.Values;

    public static bool operator ==(Currency? left, Currency? right) => Equals(left, right);

    public static bool operator !=(Currency? left, Currency? right) => !Equals(left, right);

    public static Result<Currency> FromCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return MoneyErrors.CurrencyRequired;
        }

        return KnownCurrencies.TryGetValue(code.Trim().ToUpperInvariant(), out var currency)
            ? currency
            : MoneyErrors.UnknownCurrency;
    }

    public bool Equals(Currency? other) => other is not null && string.Equals(Code, other.Code, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is Currency other && Equals(other);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Code);

    public override string ToString() => Code;
}
