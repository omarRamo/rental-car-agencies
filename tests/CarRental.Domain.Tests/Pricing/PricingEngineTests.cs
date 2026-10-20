using CarRental.Domain.Common;
using CarRental.Domain.Pricing;
using static CarRental.Domain.Tests.Pricing.PricingTestData;

namespace CarRental.Domain.Tests.Pricing;

/// <summary>The pricing engine against docs/conception/phase-1-exploitation.md §4 and the SPEC §7 worked example.</summary>
public sealed class PricingEngineTests
{
    [Fact]
    public void ReferenceExample()
    {
        // SPEC §7 / PROMPT §7: ECMR, 28/06/2027 10:00 → 03/07/2027 10:00 Africa/Tunis, mid season 110.000 until 30/06,
        // high season 140.000 from 01/07, tier 3–6 days −5 %, additional driver 10.000/day, child seat 8.000/day (cap 50.000).
        var quote = Quote(ReferencePlan().Build(), ReferenceRequest());

        quote.Days.ShouldBe(5);
        quote.Lines.Select(l => (l.Kind, l.Quantity, l.UnitPrice.Amount, l.InclVat.Amount)).ShouldBe(
        [
            (PriceLineKind.Rental, 3, 110.000m, 330.000m),
            (PriceLineKind.Rental, 2, 140.000m, 280.000m),
            (PriceLineKind.DurationDiscount, 1, -30.500m, -30.500m),
            (PriceLineKind.Extra, 5, 10.000m, 50.000m),
            (PriceLineKind.Extra, 5, 8.000m, 40.000m),
            (PriceLineKind.DailyContribution, 5, 2.380m, 11.900m),
        ]);
        quote.TotalInclVat.ShouldBe(Money.Tnd(681.400m));
        quote.TotalExclVat.ShouldBe(Money.Tnd(572.605m));
        quote.TotalVat.ShouldBe(Money.Tnd(108.795m));

        // TTC lines: HT = round(669.500 / 1.19) = 562.605 split over the lines; contribution: 5 × 2.000 HT + 19 % VAT.
        quote.Lines.Select(l => l.ExclVat.Amount).ShouldBe([277.311m, 235.294m, -25.630m, 42.017m, 33.613m, 10.000m]);
        quote.Lines.Select(l => l.Vat.Amount).ShouldBe([52.689m, 44.706m, -4.870m, 7.983m, 6.387m, 1.900m]);
        quote.Lines.ShouldAllBe(l => l.VatRate == 0.19m);
        quote.RentalSubtotal.ShouldBe(Money.Tnd(610m));
        quote.Deposit.ShouldBe(Money.Tnd(600m));
        quote.IncludesFranchiseWaiver.ShouldBeFalse();
        quote.AppliedPromotions.ShouldBeEmpty();
        quote.PromoCodeStatus.ShouldBe(PromoCodeStatus.None);
    }

    [Fact]
    public void Reference_example_lines_carry_codes_and_localizable_labels()
    {
        var quote = Quote(ReferencePlan().Build(), ReferenceRequest());

        quote.Lines.Select(l => l.Code).ShouldBe(
            [QuoteLineCodes.Rental, QuoteLineCodes.Rental, QuoteLineCodes.DurationDiscount, "ADDITIONAL_DRIVER", "CHILD_SEAT", QuoteLineCodes.DailyContribution]);
        quote.Lines.Select(l => l.LabelKey).ShouldBe(
        [
            QuoteLabels.RentalMidSeason,
            QuoteLabels.RentalHighSeason,
            QuoteLabels.DurationDiscount,
            QuoteLabels.Extra,
            QuoteLabels.Extra,
            QuoteLabels.DailyContribution,
        ]);
        quote.Lines[2].LabelArguments.ShouldBe(["3", "6", "5"]);
        quote.Lines[3].LabelArguments.ShouldBe(["Conducteur additionnel", "Additional driver"]);
        quote.Lines[5].LabelArguments.ShouldBe(["2.000"]);
        quote.DailyRates.Select(r => (r.Date, r.Period.Kind, r.Price.Amount)).ShouldBe(
        [
            (new DateOnly(2027, 6, 28), RatePeriodKind.MidSeason, 110m),
            (new DateOnly(2027, 6, 29), RatePeriodKind.MidSeason, 110m),
            (new DateOnly(2027, 6, 30), RatePeriodKind.MidSeason, 110m),
            (new DateOnly(2027, 7, 1), RatePeriodKind.HighSeason, 140m),
            (new DateOnly(2027, 7, 2), RatePeriodKind.HighSeason, 140m),
        ]);
    }

    [Fact]
    public void Return_at_10_59_stays_within_the_tolerance()
    {
        var quote = Quote(ReferencePlan().Build(), ReferenceRequest(Tunis(2027, 7, 3, 10, 59)));

        quote.Days.ShouldBe(5);
        quote.TotalInclVat.ShouldBe(Money.Tnd(681.400m));
    }

    [Fact]
    public void Return_at_11_00_adds_a_day_at_the_price_of_its_period()
    {
        var quote = Quote(ReferencePlan().Build(), ReferenceRequest(Tunis(2027, 7, 3, 11, 0)));

        quote.Days.ShouldBe(6);
        quote.Lines.Select(l => (l.Kind, l.Quantity, l.InclVat.Amount)).ShouldBe(
        [
            (PriceLineKind.Rental, 3, 330.000m),
            (PriceLineKind.Rental, 3, 420.000m),
            (PriceLineKind.DurationDiscount, 1, -37.500m),
            (PriceLineKind.Extra, 6, 60.000m),
            (PriceLineKind.Extra, 6, 48.000m),
            (PriceLineKind.DailyContribution, 6, 14.280m),
        ]);
        quote.TotalInclVat.ShouldBe(Money.Tnd(834.780m));
    }

    [Fact]
    public void Group_rounding_remainder_goes_to_the_largest_line()
    {
        // 820.500 TTC at 19 %: group HT 689.496, line HT sum 689.495 → the millime goes to the 420.000 line.
        var quote = Quote(ReferencePlan().Build(), ReferenceRequest(Tunis(2027, 7, 3, 11, 0)));

        quote.Lines.Select(l => l.ExclVat.Amount).ShouldBe([277.311m, 352.942m, -31.513m, 50.420m, 40.336m, 12.000m]);
        quote.TotalExclVat.ShouldBe(Money.Tnd(701.496m));
        quote.TotalVat.ShouldBe(Money.Tnd(133.284m));
        quote.Lines.ShouldAllBe(l => l.ExclVat + l.Vat == l.InclVat);
    }

    [Theory]
    [InlineData(10, 0, 10, 30, 1)]
    [InlineData(10, 0, 10, 59, 1)]
    [InlineData(10, 0, 9, 0, 1)]
    public void A_short_rental_counts_one_day(int pickupHour, int pickupMinute, int returnHour, int returnMinute, int expectedDays)
    {
        var returnDay = returnHour > pickupHour || (returnHour == pickupHour && returnMinute > pickupMinute) ? 28 : 29;
        var request = Request(Tunis(2027, 6, 28, pickupHour, pickupMinute), Tunis(2027, 6, returnDay, returnHour, returnMinute));

        Quote(ReferencePlan().Build(), request).Days.ShouldBe(expectedDays);
    }

    [Fact]
    public void The_tolerance_comes_from_the_parameters()
    {
        var parameters = Parameters with { LateReturnTolerance = TimeSpan.FromMinutes(120) };

        var quote = Quote(ReferencePlan().Build(), ReferenceRequest(Tunis(2027, 7, 3, 11, 30)), parameters: parameters);

        quote.Days.ShouldBe(5);
    }

    [Fact]
    public void A_stay_straddling_a_priority_event_takes_the_event_price_on_its_days()
    {
        var plan = ReferencePlan()
            .Event("Aïd", new DateOnly(2027, 6, 29), new DateOnly(2027, 6, 30))
            .EventPrice(Ecmr, "Aïd", 150m)
            .Build();

        var quote = Quote(plan, Request(Tunis(2027, 6, 27), Tunis(2027, 7, 2)));

        quote.Lines.Where(l => l.Kind == PriceLineKind.Rental).Select(l => (l.LabelKey, l.Quantity, l.UnitPrice.Amount)).ShouldBe(
        [
            (QuoteLabels.RentalMidSeason, 2, 110m),
            (QuoteLabels.RentalEvent, 2, 150m),
            (QuoteLabels.RentalHighSeason, 1, 140m),
        ]);
        quote.Lines.Single(l => l.LabelKey == QuoteLabels.RentalEvent).LabelArguments.ShouldBe(["Aïd"]);
        quote.RentalSubtotal.ShouldBe(Money.Tnd(660m));
    }

    [Fact]
    public void Overlapping_events_use_the_highest_priority()
    {
        var plan = ReferencePlan()
            .Event("Aïd", new DateOnly(2027, 6, 29), new DateOnly(2027, 6, 30), priority: 1)
            .Event("Festival", new DateOnly(2027, 6, 30), new DateOnly(2027, 7, 1), priority: 2)
            .EventPrice(Ecmr, "Aïd", 150m)
            .EventPrice(Ecmr, "Festival", 170m)
            .Build();

        var quote = Quote(plan, Request(Tunis(2027, 6, 29), Tunis(2027, 7, 2)));

        quote.DailyRates.Select(r => r.Price.Amount).ShouldBe([150m, 170m, 170m]);
    }

    [Fact]
    public void An_event_without_price_for_the_category_falls_back_to_the_season_then_the_base_price()
    {
        var plan = ReferencePlan()
            .Prices(Mcmr, basePrice: 90m)
            .Event("Festival", new DateOnly(2027, 6, 29), new DateOnly(2027, 6, 30))
            .EventPrice(Mcmr, "Festival", 130m)
            .Build();

        Quote(plan, Request(Tunis(2027, 6, 29), Tunis(2027, 7, 1))).DailyRates.Select(r => r.Price.Amount).ShouldBe([110m, 110m]);
        Quote(plan, Request(Tunis(2027, 6, 28), Tunis(2027, 7, 1), categoryId: Mcmr)).DailyRates.Select(r => r.Price.Amount)
            .ShouldBe([90m, 130m, 130m]);
    }

    [Fact]
    public void Days_outside_every_season_take_the_base_price()
    {
        var quote = Quote(ReferencePlan().Build(), Request(Tunis(2027, 3, 30), Tunis(2027, 4, 2)));

        quote.Lines.Where(l => l.Kind == PriceLineKind.Rental).Select(l => (l.LabelKey, l.Quantity, l.UnitPrice.Amount)).ShouldBe(
        [
            (QuoteLabels.RentalBase, 2, 100m),
            (QuoteLabels.RentalMidSeason, 1, 110m),
        ]);
    }

    [Fact]
    public void Each_day_takes_the_tunis_date_of_pickup_plus_whole_days()
    {
        // Pick-up 30/06 23:30 Tunis (22:30 UTC): day 0 is 30/06 (mid), day 1 is 01/07 (high).
        var quote = Quote(ReferencePlan().Build(), Request(Tunis(2027, 6, 30, 23, 30), Tunis(2027, 7, 2, 23, 30)));

        quote.DailyRates.Select(r => r.Date).ShouldBe([new DateOnly(2027, 6, 30), new DateOnly(2027, 7, 1)]);
    }

    [Fact]
    public void Minimum_duration_not_met_is_an_explicit_error_with_the_required_minimum()
    {
        var plan = ReferencePlan().Build();
        var highSeason = plan.Seasons.Single(s => s.Level == SeasonLevel.High);
        plan.ReplaceSeasons(
            plan.Seasons.Select(s => new SeasonDefinition(s.Level, s.StartDate, s.EndDate, s.Level == SeasonLevel.High ? 3 : 0, s.Id)),
            Now).IsSuccess.ShouldBeTrue();
        plan.Seasons.Single(s => s.Id == highSeason.Id).MinimumDays.ShouldBe(3);

        var result = Calculate(plan, Request(Tunis(2027, 7, 10), Tunis(2027, 7, 12)));

        result.Error.Code.ShouldBe("pricing.minimum_duration_not_met");
        PricingErrors.TryGetRequiredMinimumDays(result.Error, out var required).ShouldBeTrue();
        required.ShouldBe(3);
        Calculate(plan, Request(Tunis(2027, 7, 10), Tunis(2027, 7, 13))).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void The_minimum_is_the_largest_one_among_the_periods_touched_by_the_rental_days()
    {
        var plan = new PlanBuilder()
            .Season(SeasonLevel.Mid, new DateOnly(2027, 4, 1), new DateOnly(2027, 6, 30))
            .Season(SeasonLevel.High, new DateOnly(2027, 7, 1), new DateOnly(2027, 8, 31), minimumDays: 3)
            .Event("Fin de saison", new DateOnly(2027, 8, 30), new DateOnly(2027, 9, 2), minimumDays: 5)
            .Prices(Ecmr, basePrice: 100m, mid: 110m, high: 140m)
            .Build();

        // The return day (01/07) is not a rental day: only the mid season is touched.
        Calculate(plan, Request(Tunis(2027, 6, 29), Tunis(2027, 7, 1))).IsSuccess.ShouldBeTrue();

        // 30/06 and 01/07 are rental days: the high season (3 days) is touched.
        var touchingHigh = Calculate(plan, Request(Tunis(2027, 6, 30), Tunis(2027, 7, 2)));
        PricingErrors.TryGetRequiredMinimumDays(touchingHigh.Error, out var high).ShouldBeTrue();
        high.ShouldBe(3);

        // The event (5 days) overlaps the high season (3 days): the largest minimum applies.
        var touchingEvent = Calculate(plan, Request(Tunis(2027, 8, 27), Tunis(2027, 8, 31)));
        PricingErrors.TryGetRequiredMinimumDays(touchingEvent.Error, out var eventMinimum).ShouldBeTrue();
        eventMinimum.ShouldBe(5);
        Calculate(plan, Request(Tunis(2027, 8, 27), Tunis(2027, 9, 1))).IsSuccess.ShouldBeTrue();
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(2, 0)]
    [InlineData(3, 5)]
    [InlineData(6, 5)]
    [InlineData(7, 10)]
    [InlineData(13, 10)]
    [InlineData(14, 15)]
    [InlineData(29, 15)]
    [InlineData(30, 20)]
    [InlineData(60, 20)]
    public void Duration_tiers_switch_at_their_bounds(int days, int expectedPercent)
    {
        var plan = new PlanBuilder().Prices(Ecmr, basePrice: 100m).StandardTiers().Build();

        var quote = Quote(plan, Request(Tunis(2027, 1, 4), Tunis(2027, 1, 4).AddDays(days)));

        quote.Days.ShouldBe(days);
        var discount = quote.Lines.SingleOrDefault(l => l.Kind == PriceLineKind.DurationDiscount);
        if (expectedPercent == 0)
        {
            discount.ShouldBeNull();
        }
        else
        {
            discount.ShouldNotBeNull();
            discount.InclVat.ShouldBe(Money.Tnd(-100m * days * expectedPercent / 100m));
        }
    }

    [Theory]
    [InlineData(2, null, null, null)]
    [InlineData(3, QuoteLabels.DurationDiscount, "3", "6")]
    [InlineData(13, QuoteLabels.DurationDiscount, "7", "13")]
    [InlineData(29, QuoteLabels.DurationDiscount, "14", "29")]
    [InlineData(30, QuoteLabels.DurationDiscountOpen, "30", "20")]
    public void Tier_labels_show_the_bounds_of_the_tier(int days, string? labelKey, string? first, string? second)
    {
        var plan = new PlanBuilder().Prices(Ecmr, basePrice: 100m).StandardTiers().Build();

        var discount = Quote(plan, Request(Tunis(2027, 1, 4), Tunis(2027, 1, 4).AddDays(days))).Lines
            .SingleOrDefault(l => l.Kind == PriceLineKind.DurationDiscount);

        if (labelKey is null)
        {
            discount.ShouldBeNull();
            return;
        }

        discount.ShouldNotBeNull();
        discount.LabelKey.ShouldBe(labelKey);
        discount.LabelArguments[0].ShouldBe(first);
        discount.LabelArguments[1].ShouldBe(second);
    }

    [Fact]
    public void A_category_can_override_the_tier_percentage()
    {
        var plan = ReferencePlan().TierDiscount(3, Ecmr, 8m).Build();

        var quote = Quote(plan, ReferenceRequest());

        quote.Lines.Single(l => l.Kind == PriceLineKind.DurationDiscount).InclVat.ShouldBe(Money.Tnd(-48.800m));
        quote.Lines.Single(l => l.Kind == PriceLineKind.DurationDiscount).LabelArguments.ShouldBe(["3", "6", "8"]);
    }

    [Fact]
    public void Discounts_are_rounded_to_the_millime_away_from_zero()
    {
        // 3 × 8.230 = 24.690; 5 % = 1.2345 → 1.235 (banker's rounding would give 1.234).
        var plan = new PlanBuilder().Prices(Ecmr, basePrice: 8.23m).StandardTiers().Build();

        var quote = Quote(plan, Request(Tunis(2027, 1, 4), Tunis(2027, 1, 7)), parameters: Parameters with { RoundDailyPricesToDinar = false });

        quote.Lines.Single(l => l.Kind == PriceLineKind.DurationDiscount).InclVat.ShouldBe(Money.Tnd(-1.235m));
    }

    [Fact]
    public void Channel_markup_is_applied_to_the_daily_prices_then_rounded_to_the_dinar()
    {
        var quote = Quote(ReferencePlan().Build(), ReferenceRequest(), ChannelPricing.Markup(15m));

        // 110 × 1.15 = 126.5 → 127; 140 × 1.15 = 161.
        quote.DailyRates.Select(r => (r.GridPrice.Amount, r.Price.Amount)).ShouldBe(
            [(110m, 127m), (110m, 127m), (110m, 127m), (140m, 161m), (140m, 161m)]);
        quote.Lines.Where(l => l.Kind == PriceLineKind.Rental).Select(l => l.InclVat.Amount).ShouldBe([381m, 322m]);
        quote.Lines.Single(l => l.Kind == PriceLineKind.DurationDiscount).InclVat.ShouldBe(Money.Tnd(-35.150m));
    }

    [Fact]
    public void Without_dinar_rounding_the_marked_up_price_keeps_its_millimes()
    {
        var quote = Quote(
            ReferencePlan().Build(),
            ReferenceRequest(),
            ChannelPricing.Markup(15m),
            Parameters with { RoundDailyPricesToDinar = false });

        quote.DailyRates.Select(r => r.Price.Amount).ShouldBe([126.5m, 126.5m, 126.5m, 161m, 161m]);
    }

    [Fact]
    public void Dinar_rounding_also_applies_at_parity_and_extras_are_never_marked_up()
    {
        var plan = new PlanBuilder()
            .Prices(Ecmr, basePrice: 110.5m)
            .Extra("GPS", "GPS", ExtraKind.Optional, ExtraPricingMode.PerDay, 5.5m)
            .Build();

        var quote = Quote(plan, Request(Tunis(2027, 1, 4), Tunis(2027, 1, 5), [new ExtraSelection("GPS", 1)]), ChannelPricing.Markup(10m));

        quote.DailyRates.Single().Price.ShouldBe(Money.Tnd(122m));
        quote.Lines.Single(l => l.Code == "GPS").InclVat.ShouldBe(Money.Tnd(5.5m));
        Quote(plan, Request(Tunis(2027, 1, 4), Tunis(2027, 1, 5))).DailyRates.Single().Price.ShouldBe(Money.Tnd(111m));
    }

    [Fact]
    public void Per_day_extras_are_capped_per_unit()
    {
        // 10 days × 8.000 = 80.000 per seat, capped at 50.000.
        var plan = ReferencePlan().Build();

        var one = Quote(plan, Request(Tunis(2027, 7, 1), Tunis(2027, 7, 11), [new ExtraSelection("CHILD_SEAT", 1)]));
        var two = Quote(plan, Request(Tunis(2027, 7, 1), Tunis(2027, 7, 11), [new ExtraSelection("child_seat", 2)]));

        var seat = one.Lines.Single(l => l.Code == "CHILD_SEAT");
        (seat.Quantity, seat.UnitPrice.Amount, seat.InclVat.Amount).ShouldBe((1, 50m, 50m));
        seat.LabelKey.ShouldBe(QuoteLabels.ExtraCapped);
        seat.LabelArguments.ShouldBe(["Siège enfant", "Child seat", "50.000"]);
        two.Lines.Single(l => l.Code == "CHILD_SEAT").InclVat.ShouldBe(Money.Tnd(100m));
    }

    [Fact]
    public void Per_rental_extras_are_charged_once_per_unit_and_selections_of_one_code_add_up()
    {
        var plan = ReferencePlan()
            .Extra("HOTEL_DELIVERY", "Livraison à l'hôtel", ExtraKind.Optional, ExtraPricingMode.PerRental, 30m)
            .Build();

        var quote = Quote(plan, ReferenceRequest() with
        {
            Extras = [new ExtraSelection("HOTEL_DELIVERY", 1), new ExtraSelection("hotel_delivery", 1)],
        });

        var line = quote.Lines.Single(l => l.Code == "HOTEL_DELIVERY");
        (line.Quantity, line.UnitPrice.Amount, line.InclVat.Amount).ShouldBe((2, 30m, 60m));
        line.LabelArguments.ShouldBe(["Livraison à l'hôtel", "Livraison à l'hôtel"]);
    }

    [Fact]
    public void Young_drivers_pay_the_young_driver_extra_each()
    {
        var plan = ReferencePlan()
            .Extra("YOUNG_DRIVER", "Jeune conducteur", ExtraKind.YoungDriver, ExtraPricingMode.PerDay, 15m)
            .Build();

        // At pick-up (28/06/2027): 24 (young), 25 exactly (not young), 37.
        var quote = Quote(plan, ReferenceRequest() with
        {
            DriverBirthDates = [new DateOnly(2002, 6, 29), new DateOnly(2002, 6, 28), new DateOnly(1990, 5, 5)],
        });

        var line = quote.Lines.Single(l => l.Kind == PriceLineKind.YoungDriver);
        (line.Code, line.Quantity, line.UnitPrice.Amount, line.InclVat.Amount).ShouldBe(("YOUNG_DRIVER", 5, 15m, 75m));
        quote.DriverAges.ShouldBe([24, 25, 37]);
        Quote(plan, ReferenceRequest()).Lines.ShouldNotContain(l => l.Kind == PriceLineKind.YoungDriver);
    }

    [Fact]
    public void The_young_driver_threshold_comes_from_the_parameters()
    {
        var plan = ReferencePlan()
            .Extra("YOUNG_DRIVER", "Jeune conducteur", ExtraKind.YoungDriver, ExtraPricingMode.PerRental, 40m)
            .Build();
        var request = ReferenceRequest() with { DriverBirthDates = [new DateOnly(2001, 1, 1)] };

        Quote(plan, request).Lines.ShouldNotContain(l => l.Kind == PriceLineKind.YoungDriver);
        Quote(plan, request, parameters: Parameters with { YoungDriverAgeBelow = 30 }).Lines
            .Single(l => l.Kind == PriceLineKind.YoungDriver).InclVat.ShouldBe(Money.Tnd(40m));
    }

    [Fact]
    public void A_return_at_another_location_adds_the_one_way_fee()
    {
        var plan = ReferencePlan()
            .Extra("ONE_WAY", "Aller simple", ExtraKind.OneWay, ExtraPricingMode.PerRental, 60m)
            .Build();

        var oneWay = Quote(plan, ReferenceRequest() with { ReturnLocationId = Airport });
        var roundTrip = Quote(plan, ReferenceRequest());

        var line = oneWay.Lines.Single(l => l.Kind == PriceLineKind.OneWay);
        (line.Quantity, line.InclVat.Amount).ShouldBe((1, 60m));
        roundTrip.Lines.ShouldNotContain(l => l.Kind == PriceLineKind.OneWay);
    }

    [Fact]
    public void Delivery_fees_apply_to_the_pickup_and_to_the_return_location()
    {
        var plan = ReferencePlan()
            .Extra("DELIVERY_HOTEL", "Livraison hôtel", ExtraKind.LocationDelivery, ExtraPricingMode.PerRental, 30m, locationId: Hotel)
            .Extra("DELIVERY_TUN", "Aéroport Tunis-Carthage", ExtraKind.LocationDelivery, ExtraPricingMode.PerRental, 0m, locationId: Airport)
            .Build();

        var both = Quote(plan, ReferenceRequest() with { PickupLocationId = Hotel, ReturnLocationId = Hotel });
        var airport = Quote(plan, ReferenceRequest() with { PickupLocationId = Airport, ReturnLocationId = Office });
        var office = Quote(plan, ReferenceRequest());

        both.Lines.Single(l => l.Kind == PriceLineKind.Delivery).ShouldSatisfyAllConditions(
            l => l.Code.ShouldBe("DELIVERY_HOTEL"),
            l => l.Quantity.ShouldBe(2),
            l => l.InclVat.ShouldBe(Money.Tnd(60m)));
        airport.Lines.Single(l => l.Kind == PriceLineKind.Delivery).ShouldSatisfyAllConditions(
            l => l.Code.ShouldBe("DELIVERY_TUN"),
            l => l.InclVat.ShouldBe(Money.Zero));
        office.Lines.ShouldNotContain(l => l.Kind == PriceLineKind.Delivery);
    }

    [Fact]
    public void Pickups_and_returns_outside_the_opening_hours_pay_the_after_hours_fee_each()
    {
        var plan = ReferencePlan()
            .Extra("AFTER_HOURS", "Hors horaires", ExtraKind.AfterHours, ExtraPricingMode.PerRental, 25m)
            .Build();

        var both = Quote(plan, Request(Tunis(2027, 6, 28, 7, 30), Tunis(2027, 7, 3, 21, 0)));
        var pickupOnly = Quote(plan, Request(Tunis(2027, 6, 28, 7, 59), Tunis(2027, 7, 3, 10, 0)));
        var atTheBounds = Quote(plan, Request(Tunis(2027, 6, 28, 8, 0), Tunis(2027, 7, 3, 20, 0)));

        both.Lines.Single(l => l.Kind == PriceLineKind.AfterHours).ShouldSatisfyAllConditions(
            l => l.Quantity.ShouldBe(2),
            l => l.InclVat.ShouldBe(Money.Tnd(50m)));
        pickupOnly.Lines.Single(l => l.Kind == PriceLineKind.AfterHours).InclVat.ShouldBe(Money.Tnd(25m));
        atTheBounds.Lines.ShouldNotContain(l => l.Kind == PriceLineKind.AfterHours);
    }

    [Fact]
    public void Inactive_extras_are_neither_offered_nor_applied()
    {
        var plan = ReferencePlan()
            .Extra("ONE_WAY", "Aller simple", ExtraKind.OneWay, ExtraPricingMode.PerRental, 60m, active: false)
            .Extra("GPS", "GPS", ExtraKind.Optional, ExtraPricingMode.PerDay, 5m, active: false)
            .Build();

        Quote(plan, ReferenceRequest() with { ReturnLocationId = Airport }).Lines.ShouldNotContain(l => l.Kind == PriceLineKind.OneWay);
        Calculate(plan, ReferenceRequest() with { Extras = [new ExtraSelection("GPS", 1)] }).Error.ShouldBe(PricingErrors.ExtraUnknown);
    }

    [Fact]
    public void The_franchise_waiver_is_charged_per_day_and_switches_to_the_reduced_deposit()
    {
        var plan = ReferencePlan()
            .Extra("FRANCHISE_WAIVER", "Rachat de franchise", ExtraKind.FranchiseWaiver, ExtraPricingMode.PerDay, 20m)
            .Build();

        var quote = Quote(plan, ReferenceRequest() with { Extras = [new ExtraSelection("FRANCHISE_WAIVER", 1)] });

        quote.Lines.Single(l => l.Kind == PriceLineKind.FranchiseWaiver).InclVat.ShouldBe(Money.Tnd(100m));
        quote.Deposit.ShouldBe(Money.Tnd(300m));
        quote.IncludesFranchiseWaiver.ShouldBeTrue();
        Calculate(plan, ReferenceRequest() with { Extras = [new ExtraSelection("FRANCHISE_WAIVER", 2)] }).Error
            .ShouldBe(PricingErrors.ExtraQuantityInvalid);
    }

    [Fact]
    public void The_guaranteed_model_adds_its_daily_supplement()
    {
        var vehicle = new GuaranteedModelSelection(Guid.CreateVersion7(), "Peugeot 208 (123 TU 4567)", Money.Tnd(15m));

        var quote = Quote(ReferencePlan().Build(), ReferenceRequest() with { GuaranteedModel = vehicle });

        var line = quote.Lines.Single(l => l.Kind == PriceLineKind.GuaranteedModel);
        (line.Code, line.Quantity, line.UnitPrice.Amount, line.InclVat.Amount).ShouldBe((QuoteLineCodes.GuaranteedModel, 5, 15m, 75m));
        line.LabelKey.ShouldBe(QuoteLabels.GuaranteedModel);
        line.LabelArguments.ShouldBe(["Peugeot 208 (123 TU 4567)"]);
        quote.TotalInclVat.ShouldBe(Money.Tnd(756.400m));
    }

    [Fact]
    public void Extras_follow_the_catalogue_order_whatever_the_request_order()
    {
        var quote = Quote(ReferencePlan().Build(), ReferenceRequest() with
        {
            Extras = [new ExtraSelection("CHILD_SEAT", 1), new ExtraSelection("ADDITIONAL_DRIVER", 1)],
        });

        quote.Lines.Where(l => l.Kind == PriceLineKind.Extra).Select(l => l.Code).ShouldBe(["ADDITIONAL_DRIVER", "CHILD_SEAT"]);
    }

    [Fact]
    public void Without_daily_contribution_there_is_no_contribution_line()
    {
        var quote = Quote(ReferencePlan().Build(), ReferenceRequest(), parameters: Parameters with { DailyContributionExclVat = Money.Zero });

        quote.Lines.ShouldNotContain(l => l.Kind == PriceLineKind.DailyContribution);
        quote.TotalInclVat.ShouldBe(Money.Tnd(669.500m));
    }

    [Fact]
    public void Request_errors_are_explicit()
    {
        var plan = ReferencePlan()
            .Extra("YOUNG_DRIVER", "Jeune conducteur", ExtraKind.YoungDriver, ExtraPricingMode.PerDay, 15m)
            .Build();

        Calculate(plan, ReferenceRequest() with { CategoryId = Mcmr }).Error.ShouldBe(PricingErrors.CategoryNotPriced);
        Calculate(plan, ReferenceRequest() with { Extras = [new ExtraSelection("UNKNOWN", 1)] }).Error.ShouldBe(PricingErrors.ExtraUnknown);
        Calculate(plan, ReferenceRequest() with { Extras = [new ExtraSelection("YOUNG_DRIVER", 1)] }).Error.ShouldBe(PricingErrors.ExtraNotSelectable);
        Calculate(plan, ReferenceRequest() with { Extras = [new ExtraSelection("CHILD_SEAT", 0)] }).Error.ShouldBe(PricingErrors.ExtraQuantityInvalid);
        Calculate(plan, ReferenceRequest() with { Extras = [new ExtraSelection("CHILD_SEAT", 11)] }).Error.ShouldBe(PricingErrors.ExtraQuantityInvalid);
        Calculate(plan, ReferenceRequest() with { DriverBirthDates = [new DateOnly(2027, 6, 29)] }).Error.ShouldBe(PricingErrors.DriversInvalid);
        Calculate(plan, ReferenceRequest() with { DriverBirthDates = Enumerable.Repeat(new DateOnly(1990, 1, 1), 6).ToList() }).Error
            .ShouldBe(PricingErrors.DriversInvalid);
        Calculate(plan, Request(Tunis(2027, 1, 1), Tunis(2028, 1, 2))).Error.ShouldBe(PricingErrors.RentalTooLong);
    }

    [Fact]
    public void The_calculation_records_its_inputs_and_plan()
    {
        var plan = ReferencePlan().Build();
        var request = ReferenceRequest();

        var quote = Quote(plan, request, ChannelPricing.Markup(15m));

        quote.RatePlanId.ShouldBe(plan.Id);
        quote.RatePlanVersion.ShouldBeNull();
        quote.Request.ShouldBe(request);
        quote.Parameters.ShouldBe(Parameters);
        quote.ChannelPricing.ShouldBe(ChannelPricing.Markup(15m));
        quote.Deposits.ShouldBe(Deposits);
    }
}
