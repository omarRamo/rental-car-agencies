using CarRental.Domain.Common;
using CarRental.Domain.Fleet;

namespace CarRental.Application.Fleet;

/// <summary>Conversions of form input (decimal amounts typed in dinars) into domain values.</summary>
internal static class FleetInput
{
    /// <summary>A non-negative amount in TND with three decimals at most, or <see cref="FleetErrors.AmountInvalid"/>.</summary>
    public static Result<Money> Amount(decimal amount)
    {
        var money = Money.FromExact(amount, Currency.Tnd);
        return money.IsSuccess && !money.Value.IsNegative ? money.Value : FleetErrors.AmountInvalid;
    }

    public static Result<Money?> OptionalAmount(decimal? amount)
    {
        if (amount is not { } value)
        {
            return Result.Success<Money?>(null);
        }

        var money = Amount(value);
        return money.IsSuccess ? Result.Success<Money?>(money.Value) : Result.Failure<Money?>(money.Error);
    }
}
