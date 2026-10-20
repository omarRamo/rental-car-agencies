using CarRental.Domain.Common;

namespace CarRental.Application.Pricing.Contracts;

/// <summary>
/// The full, immutable price of a rental, ready to be frozen by the Bookings module as a quote version (Quote and
/// QuoteLine): lines, totals, charged days, deposit (not part of the total), the rate plan version used, the applied
/// promotions, the price of each day and the inputs the calculation was made from.
/// </summary>
/// <remarks>
/// Invariants: Σ line TTC = <see cref="TotalInclVat"/>, Σ line HT = <see cref="TotalExclVat"/>, Σ line VAT =
/// <see cref="TotalVat"/>, HT + VAT = TTC on every line; amounts in millimes, never a negative total.
/// </remarks>
public sealed record PriceQuote(
    Guid RatePlanId,
    int? RatePlanVersion,
    int Days,
    IReadOnlyList<PriceQuoteLine> Lines,
    Money TotalExclVat,
    Money TotalVat,
    Money TotalInclVat,
    Money RentalSubtotal,
    Money Deposit,
    bool IncludesFranchiseWaiver,
    IReadOnlyList<AppliedPromotionInfo> AppliedPromotions,
    PromoCodeOutcome PromoCodeOutcome,
    IReadOnlyList<DailyRateInfo> DailyRates,
    PriceQuoteInputs Inputs);

/// <summary>
/// A quote line. The label is <see cref="LabelKey"/> (resource key, e.g. <c>Quote.Line.Rental.MidSeason</c>) formatted
/// with <see cref="LabelArguments"/> (invariant strings: numbers with a dot, names). <see cref="Code"/> is the extra code
/// for extras, else <c>RENTAL</c>, <c>DURATION_DISCOUNT</c>, <c>PROMOTION</c>, <c>GUARANTEED_MODEL</c> or
/// <c>DAILY_CONTRIBUTION</c>. <see cref="UnitPrice"/> is VAT included.
/// </summary>
public sealed record PriceQuoteLine(
    QuoteLineKind Kind,
    string Code,
    string LabelKey,
    IReadOnlyList<string> LabelArguments,
    int Quantity,
    Money UnitPrice,
    Money ExclVat,
    Money Vat,
    Money InclVat,
    decimal VatRate);

/// <summary>Kind of a quote line (same values as the domain enum).</summary>
public enum QuoteLineKind
{
    Rental = 1,
    DurationDiscount = 2,
    Promotion = 3,
    GuaranteedModel = 4,
    Extra = 5,
    FranchiseWaiver = 6,
    YoungDriver = 7,
    Delivery = 8,
    AfterHours = 9,
    OneWay = 10,
    DailyContribution = 11,
}

/// <summary>Outcome of the promotion code of the request (same values as the domain enum).</summary>
public enum PromoCodeOutcome
{
    None = 0,
    Applied = 1,
    Unknown = 2,
    NotApplicable = 3,
    NotRetained = 4,
}

/// <summary>Trigger of a promotion (same values as the domain enum).</summary>
public enum PromotionKindInfo
{
    Code = 1,
    EarlyBooking = 2,
    LongStay = 3,
}

/// <summary>A promotion applied to the quote and the discount it granted (positive amount).</summary>
public sealed record AppliedPromotionInfo(Guid PromotionId, PromotionKindInfo Kind, string? Code, string Name, Money Amount, bool IsCombinable);

/// <summary>Column of the price grid (same values as the domain enum).</summary>
public enum RatePeriodKindInfo
{
    Base = 0,
    LowSeason = 1,
    MidSeason = 2,
    HighSeason = 3,
    Event = 4,
}

/// <summary>Price of one rental day: Tunis date, grid column (and event), grid price TTC and price after the channel rule.</summary>
public sealed record DailyRateInfo(DateOnly Date, RatePeriodKindInfo Period, Guid? EventId, Money GridPrice, Money Price);

/// <summary>How a channel is priced (same values as the domain enum).</summary>
public enum ChannelPricingModeInfo
{
    Parity = 1,
    Markup = 2,
    OwnGrid = 3,
}

/// <summary>
/// Snapshot of everything the calculation used, so that a frozen quote can be explained and reproduced: the request
/// (driver ages instead of birth dates), the booking instant, the channel rule and the plans, the business settings
/// and the deposits of the category.
/// </summary>
public sealed record PriceQuoteInputs(
    Guid AgencyId,
    Guid CategoryId,
    string CategoryCode,
    Guid PickupLocationId,
    Guid ReturnLocationId,
    DateTimeOffset PickupAt,
    DateTimeOffset ReturnAt,
    SalesChannel Channel,
    IReadOnlyList<SelectedExtra> Extras,
    IReadOnlyList<int> DriverAges,
    string? PromoCode,
    Guid? GuaranteedVehicleId,
    Money? GuaranteedModelDailySupplement,
    DateTimeOffset QuotedAt,
    ChannelPricingModeInfo ChannelPricingMode,
    decimal MarkupPercent,
    Guid DefaultPlanId,
    int? DefaultPlanVersion,
    decimal VatRate,
    Money DailyContributionExclVat,
    TimeSpan LateReturnTolerance,
    bool RoundDailyPricesToDinar,
    int YoungDriverAgeBelow,
    TimeOnly OpensAt,
    TimeOnly ClosesAt,
    Money CategoryDeposit,
    Money CategoryReducedDeposit);
