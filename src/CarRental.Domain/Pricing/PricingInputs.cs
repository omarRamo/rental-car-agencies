using CarRental.Domain.Common;

namespace CarRental.Domain.Pricing;

/// <summary>An extra chosen by the customer or the agent, by code (case-insensitive), with a quantity.</summary>
public sealed record ExtraSelection(string Code, int Quantity);

/// <summary>Guaranteed model ("modèle garanti"): the chosen vehicle, its label on the quote and its daily supplement TTC.</summary>
public sealed record GuaranteedModelSelection(Guid VehicleId, string VehicleLabel, Money DailySupplement);

/// <summary>
/// What is priced: a category for a rental period, between two locations, on a channel, at <see cref="QuotedAt"/>
/// (booking instant: early booking promotions and booking windows), with extras, drivers and an optional promotion code.
/// </summary>
public sealed record PricingRequest(
    Guid CategoryId,
    Period RentalPeriod,
    Guid PickupLocationId,
    Guid ReturnLocationId,
    SalesChannel Channel,
    DateTimeOffset QuotedAt,
    IReadOnlyList<ExtraSelection>? Extras = null,
    IReadOnlyList<DateOnly>? DriverBirthDates = null,
    string? PromoCode = null,
    GuaranteedModelSelection? GuaranteedModel = null);

/// <summary>Business settings used by the engine (read by the application from the Referential settings).</summary>
/// <param name="VatRate">Standard VAT rate, e.g. 0.19.</param>
/// <param name="DailyContributionExclVat">Contribution per rented vehicle and day, excluding VAT (2.000 TND).</param>
/// <param name="LateReturnTolerance">Late return tolerance before an extra day is charged (59 min).</param>
/// <param name="RoundDailyPricesToDinar">Round daily prices to the dinar after the channel rule.</param>
/// <param name="YoungDriverAgeBelow">A driver younger than this at pick-up pays the young driver extra (25).</param>
/// <param name="OpensAt">Opening time of the agency (Tunis); a pick-up or return before it is after-hours.</param>
/// <param name="ClosesAt">Closing time of the agency (Tunis), included; a pick-up or return after it is after-hours.</param>
public sealed record PricingParameters(
    decimal VatRate,
    Money DailyContributionExclVat,
    TimeSpan LateReturnTolerance,
    bool RoundDailyPricesToDinar,
    int YoungDriverAgeBelow,
    TimeOnly OpensAt,
    TimeOnly ClosesAt);

/// <summary>Channel rule applied to the daily prices of the grid: parity, markup percentage, or the channel's own grid.</summary>
public sealed record ChannelPricing(ChannelPricingMode Mode, decimal MarkupPercent = 0m)
{
    public static ChannelPricing Parity { get; } = new(ChannelPricingMode.Parity);

    public static ChannelPricing OwnGrid { get; } = new(ChannelPricingMode.OwnGrid);

    public static ChannelPricing Markup(decimal percent) => new(ChannelPricingMode.Markup, percent);

    /// <summary>The pricing of a channel rule of a default plan (parity when there is no rule).</summary>
    public static ChannelPricing From(ChannelRule? rule) => rule?.Mode switch
    {
        null or ChannelPricingMode.Parity => Parity,
        ChannelPricingMode.Markup => Markup(rule.MarkupPercent ?? 0m),
        ChannelPricingMode.OwnGrid => OwnGrid,
        _ => throw new ArgumentOutOfRangeException(nameof(rule), rule.Mode, "Unknown channel pricing mode."),
    };
}

/// <summary>Deposits of the category (Fleet): the standard one and the one with the franchise waiver.</summary>
public sealed record DepositTerms(Money Deposit, Money ReducedDeposit);
