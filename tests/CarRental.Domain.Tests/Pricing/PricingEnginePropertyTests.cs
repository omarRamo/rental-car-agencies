using CarRental.Domain.Common;
using CarRental.Domain.Pricing;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using static CarRental.Domain.Tests.Pricing.PricingTestData;

namespace CarRental.Domain.Tests.Pricing;

/// <summary>Invariants of the engine on thousands of random requests over a demo-like plan (no minimum durations).</summary>
public sealed class PricingEnginePropertyTests
{
    private static readonly DateTimeOffset Origin = Tunis(2026, 11, 1, 0, 0);

    private static readonly RatePlan Plan = BuildPlan();

    private static readonly ChannelPricing[] ChannelPricings =
    [
        ChannelPricing.Parity,
        ChannelPricing.Markup(15m),
        ChannelPricing.Markup(12.5m),
        ChannelPricing.Markup(7.25m),
    ];

    private static readonly string?[] PromoCodes = [null, "BIENVENUE10", "OFFERT", "INCONNU"];

    private static readonly Guid[] Locations = [Office, Airport, Hotel];

    [Property(MaxTest = 1500)]
    public Property Line_amounts_add_up_to_the_totals()
    {
        return Prop.ForAll(Requests(), input =>
        {
            var result = Calculate(input);
            if (result.IsFailure)
            {
                return false;
            }

            var quote = result.Value;
            return quote.Lines.Select(l => l.InclVat).Sum() == quote.TotalInclVat
                && quote.Lines.Select(l => l.ExclVat).Sum() == quote.TotalExclVat
                && quote.Lines.Select(l => l.Vat).Sum() == quote.TotalVat
                && quote.Lines.All(l => l.ExclVat + l.Vat == l.InclVat)
                && quote.TotalExclVat + quote.TotalVat == quote.TotalInclVat;
        });
    }

    [Property(MaxTest = 1500)]
    public Property Amounts_are_in_millimes_and_the_total_is_never_negative()
    {
        return Prop.ForAll(Requests(), input =>
        {
            var quote = Calculate(input).Value;
            return !quote.TotalInclVat.IsNegative
                && !quote.TotalExclVat.IsNegative
                && quote.Lines.All(l => decimal.Round(l.InclVat.Amount, 3) == l.InclVat.Amount && decimal.Round(l.ExclVat.Amount, 3) == l.ExclVat.Amount)
                && quote.Lines.Where(l => l.Kind is not (PriceLineKind.DurationDiscount or PriceLineKind.Promotion)).All(l => !l.InclVat.IsNegative);
        });
    }

    [Property(MaxTest = 1500)]
    public Property Ttc_lines_are_split_so_that_their_ht_matches_the_group_rounding()
    {
        return Prop.ForAll(Requests(), input =>
        {
            var quote = Calculate(input).Value;
            var ttcLines = quote.Lines.Where(l => l.Kind != PriceLineKind.DailyContribution).ToList();
            var groupTtc = ttcLines.Select(l => l.InclVat).Sum();
            return ttcLines.Select(l => l.ExclVat).Sum() == Money.Tnd(groupTtc.Amount / 1.19m);
        });
    }

    [Property(MaxTest = 500)]
    public Property More_days_never_lower_the_rental_subtotal()
    {
        return Prop.ForAll(Requests(), Gen.Choose(1, 30).ToArbitrary(), (input, extraDays) =>
        {
            var longer = input with
            {
                Request = input.Request with
                {
                    RentalPeriod = Period.Create(input.Request.RentalPeriod.Start, input.Request.RentalPeriod.End.AddDays(extraDays)).Value,
                },
            };
            var shortQuote = Calculate(input);
            var longQuote = Calculate(longer);
            return longQuote.IsFailure
                ? longQuote.Error == PricingErrors.RentalTooLong
                : longQuote.Value.Days > shortQuote.Value.Days && longQuote.Value.RentalSubtotal >= shortQuote.Value.RentalSubtotal;
        });
    }

    [Property(MaxTest = 500)]
    public Property The_engine_is_deterministic()
    {
        return Prop.ForAll(Requests(), input =>
        {
            var first = Calculate(input).Value;
            var reordered = input with { Request = input.Request with { Extras = input.Request.Extras?.Reverse().ToList() } };
            var second = Calculate(reordered).Value;
            return first.Lines.Count == second.Lines.Count
                && first.Lines.Zip(second.Lines).All(pair => Same(pair.First, pair.Second))
                && first.TotalInclVat == second.TotalInclVat
                && first.Deposit == second.Deposit
                && first.PromoCodeStatus == second.PromoCodeStatus;
        });
    }

    private static bool Same(PriceLine a, PriceLine b) =>
        a.Kind == b.Kind
        && a.Code == b.Code
        && a.LabelKey == b.LabelKey
        && a.LabelArguments.SequenceEqual(b.LabelArguments)
        && a.Quantity == b.Quantity
        && a.UnitPrice == b.UnitPrice
        && a.ExclVat == b.ExclVat
        && a.Vat == b.Vat
        && a.InclVat == b.InclVat;

    private static Result<PriceCalculation> Calculate(Input input) =>
        PricingEngine.Calculate(Plan, input.ChannelPricing, input.Request, input.Parameters, Deposits);

    private static Arbitrary<Input> Requests()
    {
        var generator =
            from startMinutes in Gen.Choose(0, 700 * 24 * 60)
            from durationMinutes in Gen.Choose(1, 45 * 24 * 60)
            from leadDays in Gen.Choose(0, 120)
            from channelPricing in Gen.Elements(ChannelPricings)
            from roundToDinar in Gen.Elements(true, false)
            from seats in Gen.Choose(0, 3)
            from drivers in Gen.Choose(0, 1)
            from gps in Gen.Choose(0, 2)
            from waiver in Gen.Elements(true, false)
            from code in Gen.Elements(PromoCodes)
            from pickupLocation in Gen.Elements(Locations)
            from returnLocation in Gen.Elements(Locations)
            from birthYears in Gen.ListOf(Gen.Choose(1950, 2006)).Select(years => years.Take(3).ToList())
            from channel in Gen.Elements(SalesChannel.Web, SalesChannel.Phone, SalesChannel.BookingCom)
            select Build(
                startMinutes,
                durationMinutes,
                leadDays,
                channelPricing,
                roundToDinar,
                [("CHILD_SEAT", seats), ("ADDITIONAL_DRIVER", drivers), ("GPS", gps), ("FRANCHISE_WAIVER", waiver ? 1 : 0)],
                code,
                pickupLocation,
                returnLocation,
                birthYears,
                channel);
        return generator.ToArbitrary();
    }

    private static Input Build(
        int startMinutes,
        int durationMinutes,
        int leadDays,
        ChannelPricing channelPricing,
        bool roundToDinar,
        IEnumerable<(string Code, int Quantity)> extras,
        string? code,
        Guid pickupLocation,
        Guid returnLocation,
        List<int> birthYears,
        SalesChannel channel)
    {
        var pickup = Origin.AddMinutes(startMinutes);
        var request = Request(
            pickup,
            pickup.AddMinutes(durationMinutes),
            extras: [.. extras.Where(e => e.Quantity > 0).Select(e => new ExtraSelection(e.Code, e.Quantity))],
            pickupLocationId: pickupLocation,
            returnLocationId: returnLocation,
            channel: channel,
            quotedAt: pickup.AddDays(-leadDays),
            drivers: [.. birthYears.Select(year => new DateOnly(year, 3, 15))],
            promoCode: code);
        return new Input(request, channelPricing, Parameters with { RoundDailyPricesToDinar = roundToDinar });
    }

    private static RatePlan BuildPlan()
    {
        var builder = new PlanBuilder();
        for (var year = 2026; year <= 2028; year++)
        {
            if (year > 2026)
            {
                builder
                    .Season(SeasonLevel.Low, new DateOnly(year, 1, 1), new DateOnly(year, 3, 31))
                    .Season(SeasonLevel.Mid, new DateOnly(year, 4, 1), new DateOnly(year, 6, 30))
                    .Season(SeasonLevel.High, new DateOnly(year, 7, 1), new DateOnly(year, 8, 31))
                    .Season(SeasonLevel.Mid, new DateOnly(year, 9, 1), new DateOnly(year, 10, 31));
            }

            builder.Season(SeasonLevel.Low, new DateOnly(year, 11, 1), new DateOnly(year, 12, 31));
        }

        return builder
            .Event("Aïd", new DateOnly(2027, 5, 16), new DateOnly(2027, 5, 18), priority: 2)
            .Event("Fin d'année", new DateOnly(2027, 12, 24), new DateOnly(2028, 1, 2), priority: 1)
            .Prices(Ecmr, basePrice: 109.999m, low: 100m, mid: 110.5m, high: 140m)
            .EventPrice(Ecmr, "Aïd", 155.333m)
            .EventPrice(Ecmr, "Fin d'année", 150m)
            .StandardTiers()
            .TierDiscount(7, Ecmr, 11.25m)
            .Extra("ADDITIONAL_DRIVER", "Conducteur additionnel", ExtraKind.Optional, ExtraPricingMode.PerDay, 10m)
            .Extra("CHILD_SEAT", "Siège enfant", ExtraKind.Optional, ExtraPricingMode.PerDay, 8m, cap: 50m)
            .Extra("GPS", "GPS", ExtraKind.Optional, ExtraPricingMode.PerDay, 4.999m)
            .Extra("FRANCHISE_WAIVER", "Rachat de franchise", ExtraKind.FranchiseWaiver, ExtraPricingMode.PerDay, 20m)
            .Extra("YOUNG_DRIVER", "Jeune conducteur", ExtraKind.YoungDriver, ExtraPricingMode.PerDay, 15m, cap: 150m)
            .Extra("DELIVERY_HOTEL", "Livraison hôtel", ExtraKind.LocationDelivery, ExtraPricingMode.PerRental, 30m, locationId: Hotel)
            .Extra("DELIVERY_TUN", "Aéroport", ExtraKind.LocationDelivery, ExtraPricingMode.PerRental, 0m, locationId: Airport)
            .Extra("AFTER_HOURS", "Hors horaires", ExtraKind.AfterHours, ExtraPricingMode.PerRental, 25m)
            .Extra("ONE_WAY", "Aller simple", ExtraKind.OneWay, ExtraPricingMode.PerRental, 60m)
            .Promotion(new PromotionDefinition("Bienvenue", PromotionKind.Code, PromotionDiscountType.Percentage, DiscountPercent: 10m, Code: "BIENVENUE10"))
            .Promotion(new PromotionDefinition("Offert", PromotionKind.Code, PromotionDiscountType.FixedAmount, DiscountAmount: Money.Tnd(400m), Code: "OFFERT"))
            .Promotion(new PromotionDefinition("Anticipée", PromotionKind.EarlyBooking, PromotionDiscountType.Percentage, DiscountPercent: 5m, MinimumDays: 30))
            .Promotion(new PromotionDefinition("Long séjour", PromotionKind.LongStay, PromotionDiscountType.Percentage, DiscountPercent: 7m, MinimumDays: 14, IsCombinable: true))
            .Promotion(new PromotionDefinition("Fidélité", PromotionKind.LongStay, PromotionDiscountType.FixedAmount, DiscountAmount: Money.Tnd(250m), MinimumDays: 1, IsCombinable: true))
            .Build();
    }

    public sealed record Input(PricingRequest Request, ChannelPricing ChannelPricing, PricingParameters Parameters)
    {
        public override string ToString() =>
            $"{Request.RentalPeriod} {Request.Channel} {ChannelPricing} extras=[{string.Join(", ", (Request.Extras ?? []).Select(e => $"{e.Code}×{e.Quantity}"))}] code={Request.PromoCode}";
    }
}
