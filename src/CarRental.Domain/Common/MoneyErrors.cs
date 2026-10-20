namespace CarRental.Domain.Common;

public static class MoneyErrors
{
    public static readonly Error CurrencyRequired = Error.Validation("money.currency_required", "A currency code is required.");

    public static readonly Error UnknownCurrency = Error.Validation("money.unknown_currency", "The currency is not supported.");

    public static readonly Error TooManyDecimals = Error.Validation(
        "money.too_many_decimals",
        "The amount has more decimals than the currency minor unit allows.");
}
