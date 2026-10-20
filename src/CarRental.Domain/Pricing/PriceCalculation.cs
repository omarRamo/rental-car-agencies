using CarRental.Domain.Common;

namespace CarRental.Domain.Pricing;

/// <summary>Kind of a quote line.</summary>
public enum PriceLineKind
{
    /// <summary>Rental days of one period (base price, season level or event).</summary>
    Rental = 1,

    /// <summary>Discount of the duration tier (negative).</summary>
    DurationDiscount = 2,

    /// <summary>Applied promotion (negative).</summary>
    Promotion = 3,

    /// <summary>Daily supplement of the guaranteed vehicle.</summary>
    GuaranteedModel = 4,

    /// <summary>Extra chosen by the customer.</summary>
    Extra = 5,

    /// <summary>Franchise waiver (the deposit switches to its reduced amount).</summary>
    FranchiseWaiver = 6,

    /// <summary>Young driver surcharge (automatic).</summary>
    YoungDriver = 7,

    /// <summary>Delivery or collection at a location (automatic).</summary>
    Delivery = 8,

    /// <summary>Pick-up or return outside the opening hours (automatic).</summary>
    AfterHours = 9,

    /// <summary>Return at another location (automatic).</summary>
    OneWay = 10,

    /// <summary>Daily contribution per rented vehicle, entered excluding VAT (finance law 2026).</summary>
    DailyContribution = 11,
}

/// <summary>
/// A line of a price calculation. <see cref="LabelKey"/> is a resource key and <see cref="LabelArguments"/> its invariant
/// arguments (numbers with a dot, dates <c>yyyy-MM-dd</c>, names). Amounts: <see cref="InclVat"/> = <see cref="ExclVat"/> +
/// <see cref="Vat"/>; <see cref="InclVat"/> = <see cref="Quantity"/> × <see cref="UnitPrice"/> for lines entered VAT
/// included (the daily contribution is entered excluding VAT, its unit price TTC is indicative).
/// </summary>
public sealed record PriceLine(
    PriceLineKind Kind,
    string Code,
    string LabelKey,
    IReadOnlyList<string> LabelArguments,
    int Quantity,
    Money UnitPrice,
    Money ExclVat,
    Money Vat,
    Money InclVat,
    decimal VatRate);

/// <summary>Price of one rental day: its Tunis date, the grid column used, the grid price and the price after the channel rule.</summary>
public sealed record DailyRate(DateOnly Date, RatePeriodKey Period, Money GridPrice, Money Price);

/// <summary>A promotion applied by the engine, with the discount actually granted (positive amount).</summary>
public sealed record AppliedPromotion(Guid PromotionId, PromotionKind Kind, string? Code, string Name, Money Amount, bool IsCombinable);

/// <summary>
/// The immutable result of <see cref="PricingEngine.Calculate"/>: lines, totals, deposit (not part of the total), the plan
/// version used, the applied promotions and the inputs it was computed from.
/// </summary>
public sealed class PriceCalculation
{
    internal PriceCalculation(
        RatePlan plan,
        int days,
        IReadOnlyList<DailyRate> dailyRates,
        IReadOnlyList<PriceLine> lines,
        Money deposit,
        bool includesFranchiseWaiver,
        IReadOnlyList<AppliedPromotion> appliedPromotions,
        PromoCodeStatus promoCodeStatus,
        IReadOnlyList<int> driverAges,
        PricingRequest request,
        PricingParameters parameters,
        ChannelPricing channelPricing,
        DepositTerms deposits)
    {
        RatePlanId = plan.Id;
        RatePlanVersion = plan.Version;
        Days = days;
        DailyRates = dailyRates;
        Lines = lines;
        TotalExclVat = lines.Select(l => l.ExclVat).Sum();
        TotalVat = lines.Select(l => l.Vat).Sum();
        TotalInclVat = lines.Select(l => l.InclVat).Sum();
        RentalSubtotal = dailyRates.Select(r => r.Price).Sum();
        Deposit = deposit;
        IncludesFranchiseWaiver = includesFranchiseWaiver;
        AppliedPromotions = appliedPromotions;
        PromoCodeStatus = promoCodeStatus;
        DriverAges = driverAges;
        Request = request;
        Parameters = parameters;
        ChannelPricing = channelPricing;
        Deposits = deposits;
    }

    /// <summary>The plan whose grid was used (the channel plan for an own grid, else the default plan).</summary>
    public Guid RatePlanId { get; }

    /// <summary>Its version (<see langword="null"/> for a draft, in the simulator).</summary>
    public int? RatePlanVersion { get; }

    /// <summary>Charged rental days (24 h units from the pick-up, late return tolerance included).</summary>
    public int Days { get; }

    public IReadOnlyList<DailyRate> DailyRates { get; }

    public IReadOnlyList<PriceLine> Lines { get; }

    /// <summary>Sum of the daily prices, before the duration tier and the promotions.</summary>
    public Money RentalSubtotal { get; }

    public Money TotalExclVat { get; }

    public Money TotalVat { get; }

    public Money TotalInclVat { get; }

    /// <summary>Deposit (caution) to take at departure: not part of the total.</summary>
    public Money Deposit { get; }

    public bool IncludesFranchiseWaiver { get; }

    public IReadOnlyList<AppliedPromotion> AppliedPromotions { get; }

    public PromoCodeStatus PromoCodeStatus { get; }

    /// <summary>Age of each driver at the pick-up date (the inputs keep ages, not birth dates).</summary>
    public IReadOnlyList<int> DriverAges { get; }

    public PricingRequest Request { get; }

    public PricingParameters Parameters { get; }

    public ChannelPricing ChannelPricing { get; }

    public DepositTerms Deposits { get; }
}
