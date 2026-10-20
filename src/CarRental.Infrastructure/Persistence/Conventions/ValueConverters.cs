using CarRental.Domain.Common;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace CarRental.Infrastructure.Persistence.Conventions;

/// <summary>Stores <see cref="Money"/> as <c>decimal(18,3)</c> in TND (the only currency persisted by the platform).</summary>
public sealed class MoneyConverter : ValueConverter<Money, decimal>
{
    public MoneyConverter()
        : base(money => ToProvider(money), amount => Money.Tnd(amount))
    {
    }

    private static decimal ToProvider(Money money) =>
        money.Currency == Currency.Tnd
            ? money.Amount
            : throw new InvalidOperationException($"Only TND amounts are persisted; got {money.Currency.Code}.");
}

/// <summary>Stores an <see cref="AcrissCode"/> as its four letters.</summary>
public sealed class AcrissCodeConverter : ValueConverter<AcrissCode, string>
{
    public AcrissCodeConverter()
        : base(code => code.Value, value => AcrissCode.Create(value).Value)
    {
    }
}
