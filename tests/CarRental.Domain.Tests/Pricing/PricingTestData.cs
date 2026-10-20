using CarRental.Domain.Common;
using CarRental.Domain.Pricing;

namespace CarRental.Domain.Tests.Pricing;

/// <summary>Ids, settings and rate plans of the pricing tests (SPEC §7 reference grid and its variations).</summary>
internal static class PricingTestData
{
    public static readonly Guid AgencyId = Guid.Parse("0199a000-0000-7000-8000-000000000001");
    public static readonly Guid Ecmr = Guid.Parse("0199a000-0000-7000-8000-0000000000e1");
    public static readonly Guid Mcmr = Guid.Parse("0199a000-0000-7000-8000-0000000000e2");
    public static readonly Guid Office = Guid.Parse("0199a000-0000-7000-8000-0000000000a1");
    public static readonly Guid Airport = Guid.Parse("0199a000-0000-7000-8000-0000000000a2");
    public static readonly Guid Hotel = Guid.Parse("0199a000-0000-7000-8000-0000000000a3");

    public static readonly DateTimeOffset Now = new(2027, 1, 15, 9, 0, 0, TimeSpan.Zero);

    /// <summary>VAT 19 %, contribution 2.000 HT, tolerance 59 min, dinar rounding, young driver below 25, open 08:00–20:00.</summary>
    public static readonly PricingParameters Parameters = new(
        VatRate: 0.19m,
        DailyContributionExclVat: Money.Tnd(2m),
        LateReturnTolerance: TimeSpan.FromMinutes(59),
        RoundDailyPricesToDinar: true,
        YoungDriverAgeBelow: 25,
        OpensAt: new TimeOnly(8, 0),
        ClosesAt: new TimeOnly(20, 0));

    public static readonly DepositTerms Deposits = new(Money.Tnd(600m), Money.Tnd(300m));

    public static DateTimeOffset Tunis(int year, int month, int day, int hour = 10, int minute = 0) =>
        TunisTime.FromLocal(new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Unspecified));

    /// <summary>SPEC §7: ECMR, mid season 110.000 until 30/06/2027, high season 140.000 from 01/07, tiers 0/5/10/15/20 %.</summary>
    public static PlanBuilder ReferencePlan() => new PlanBuilder()
        .Season(SeasonLevel.Mid, new DateOnly(2027, 4, 1), new DateOnly(2027, 6, 30))
        .Season(SeasonLevel.High, new DateOnly(2027, 7, 1), new DateOnly(2027, 8, 31))
        .Prices(Ecmr, basePrice: 100m, mid: 110m, high: 140m)
        .StandardTiers()
        .Extra("ADDITIONAL_DRIVER", "Conducteur additionnel", ExtraKind.Optional, ExtraPricingMode.PerDay, 10m, nameEn: "Additional driver")
        .Extra("CHILD_SEAT", "Siège enfant", ExtraKind.Optional, ExtraPricingMode.PerDay, 8m, cap: 50m, nameEn: "Child seat");

    /// <summary>The reference request: ECMR, 28/06/2027 10:00 → 03/07/2027 10:00, one additional driver and one child seat.</summary>
    public static PricingRequest ReferenceRequest(DateTimeOffset? returnAt = null) => Request(
        Tunis(2027, 6, 28),
        returnAt ?? Tunis(2027, 7, 3),
        extras: [new ExtraSelection("ADDITIONAL_DRIVER", 1), new ExtraSelection("CHILD_SEAT", 1)]);

    public static PricingRequest Request(
        DateTimeOffset pickupAt,
        DateTimeOffset returnAt,
        IReadOnlyList<ExtraSelection>? extras = null,
        Guid? categoryId = null,
        Guid? pickupLocationId = null,
        Guid? returnLocationId = null,
        SalesChannel channel = SalesChannel.Web,
        DateTimeOffset? quotedAt = null,
        IReadOnlyList<DateOnly>? drivers = null,
        string? promoCode = null,
        GuaranteedModelSelection? guaranteedModel = null) => new(
            categoryId ?? Ecmr,
            Period.Create(pickupAt, returnAt).Value,
            pickupLocationId ?? Office,
            returnLocationId ?? pickupLocationId ?? Office,
            channel,
            quotedAt ?? Now,
            extras,
            drivers,
            promoCode,
            guaranteedModel);

    public static Result<PriceCalculation> Calculate(
        RatePlan plan,
        PricingRequest request,
        ChannelPricing? channelPricing = null,
        PricingParameters? parameters = null) =>
        PricingEngine.Calculate(plan, channelPricing ?? ChannelPricing.Parity, request, parameters ?? Parameters, Deposits);

    public static PriceCalculation Quote(
        RatePlan plan,
        PricingRequest request,
        ChannelPricing? channelPricing = null,
        PricingParameters? parameters = null)
    {
        var result = Calculate(plan, request, channelPricing, parameters);
        result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error.ToString() : null);
        return result.Value;
    }
}

/// <summary>Builds a draft rate plan through the aggregate methods (every step must succeed).</summary>
internal sealed class PlanBuilder
{
    private readonly List<SeasonDefinition> _seasons = [];
    private readonly List<PriorityEventDefinition> _events = [];
    private readonly List<(Guid CategoryId, RatePeriodKind Kind, string? EventName, decimal Price)> _prices = [];
    private readonly List<DurationTierDefinition> _tiers = [];
    private readonly List<TierCategoryDiscountDefinition> _tierDiscounts = [];
    private readonly List<ExtraDefinition> _extras = [];
    private readonly List<PromotionDefinition> _promotions = [];
    private readonly List<ChannelRuleDefinition> _channelRules = [];
    private SalesChannel? _channel;

    public PlanBuilder ForChannel(SalesChannel channel)
    {
        _channel = channel;
        return this;
    }

    public PlanBuilder Season(SeasonLevel level, DateOnly start, DateOnly end, int minimumDays = 0)
    {
        _seasons.Add(new SeasonDefinition(level, start, end, minimumDays));
        return this;
    }

    public PlanBuilder Event(string name, DateOnly start, DateOnly end, int priority = 1, int minimumDays = 0)
    {
        _events.Add(new PriorityEventDefinition(name, start, end, priority, minimumDays));
        return this;
    }

    public PlanBuilder Prices(Guid categoryId, decimal basePrice, decimal? low = null, decimal? mid = null, decimal? high = null)
    {
        _prices.Add((categoryId, RatePeriodKind.Base, null, basePrice));
        if (low is { } lowPrice)
        {
            _prices.Add((categoryId, RatePeriodKind.LowSeason, null, lowPrice));
        }

        if (mid is { } midPrice)
        {
            _prices.Add((categoryId, RatePeriodKind.MidSeason, null, midPrice));
        }

        if (high is { } highPrice)
        {
            _prices.Add((categoryId, RatePeriodKind.HighSeason, null, highPrice));
        }

        return this;
    }

    public PlanBuilder EventPrice(Guid categoryId, string eventName, decimal price)
    {
        _prices.Add((categoryId, RatePeriodKind.Event, eventName, price));
        return this;
    }

    public PlanBuilder Tier(int minimumDays, decimal percent)
    {
        _tiers.Add(new DurationTierDefinition(minimumDays, percent));
        return this;
    }

    /// <summary>1–2: 0 %, 3–6: 5 %, 7–13: 10 %, 14–29: 15 %, 30+: 20 %.</summary>
    public PlanBuilder StandardTiers() => Tier(1, 0m).Tier(3, 5m).Tier(7, 10m).Tier(14, 15m).Tier(30, 20m);

    public PlanBuilder TierDiscount(int tierMinimumDays, Guid categoryId, decimal percent)
    {
        _tierDiscounts.Add(new TierCategoryDiscountDefinition(tierMinimumDays, categoryId, percent));
        return this;
    }

    public PlanBuilder Extra(
        string code,
        string name,
        ExtraKind kind,
        ExtraPricingMode mode,
        decimal price,
        decimal? cap = null,
        Guid? locationId = null,
        bool active = true,
        string? nameEn = null)
    {
        _extras.Add(new ExtraDefinition(
            code,
            name,
            kind,
            mode,
            Money.Tnd(price),
            cap is { } capAmount ? Money.Tnd(capAmount) : null,
            locationId,
            active,
            nameEn));
        return this;
    }

    public PlanBuilder Promotion(PromotionDefinition promotion)
    {
        _promotions.Add(promotion);
        return this;
    }

    public PlanBuilder ChannelRule(SalesChannel channel, ChannelPricingMode mode, decimal? percent = null)
    {
        _channelRules.Add(new ChannelRuleDefinition(channel, mode, percent));
        return this;
    }

    public RatePlan Build()
    {
        var now = PricingTestData.Now;
        var plan = RatePlan.CreateDraft(PricingTestData.AgencyId, _channel, "Grille de test", now).Value;
        Succeed(plan.ReplaceSeasons(_seasons, now));
        Succeed(plan.ReplaceEvents(_events, now));
        var prices = _prices.Select(p => new CategoryPriceDefinition(
            p.CategoryId,
            p.Kind == RatePeriodKind.Event
                ? RatePeriodKey.ForEvent(plan.Events.Single(e => e.Name == p.EventName).Id)
                : RatePeriodKey.From(p.Kind, null)!.Value,
            Money.Tnd(p.Price)));
        Succeed(plan.ReplacePrices(prices, now));
        Succeed(plan.ReplaceTiers(_tiers, _tierDiscounts, now));
        Succeed(plan.ReplaceExtras(_extras, now));
        Succeed(plan.ReplacePromotions(_promotions, now));
        Succeed(plan.ReplaceChannelRules(_channelRules, now));
        return plan;
    }

    private static void Succeed(Result result) => result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error.ToString() : null);
}
