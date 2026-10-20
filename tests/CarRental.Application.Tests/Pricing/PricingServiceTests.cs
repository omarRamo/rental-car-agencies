using CarRental.Application.Fleet.Contracts;
using CarRental.Application.Pricing.Contracts;
using CarRental.Application.Referential.Contracts;
using CarRental.Domain.Common;
using CarRental.Domain.Pricing;
using static CarRental.Application.Tests.Pricing.PricingWorld;

namespace CarRental.Application.Tests.Pricing;

/// <summary><see cref="IPricingService"/>: plan selection by channel, settings, fleet and referential checks, snapshot.</summary>
public sealed class PricingServiceTests
{
    private readonly PricingWorld _world = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<PriceQuote> QuoteAsync(PriceRequest request)
    {
        var result = await _world.PricingService().QuoteAsync(request, Ct);
        result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error.ToString() : null);
        return result.Value;
    }

    [Fact]
    public async Task The_reference_example_is_priced_with_the_published_default_plan()
    {
        var plan = _world.PublishDefault();

        var quote = await QuoteAsync(ReferenceRequest());

        quote.TotalInclVat.ShouldBe(Money.Tnd(681.400m));
        quote.TotalExclVat.ShouldBe(Money.Tnd(572.605m));
        quote.TotalVat.ShouldBe(Money.Tnd(108.795m));
        quote.Days.ShouldBe(5);
        quote.RatePlanId.ShouldBe(plan.Id);
        quote.RatePlanVersion.ShouldBe(1);
        quote.Deposit.ShouldBe(Money.Tnd(600m));
        quote.Lines.Select(l => (l.Kind, l.InclVat.Amount)).ShouldBe(
        [
            (QuoteLineKind.Rental, 330m),
            (QuoteLineKind.Rental, 280m),
            (QuoteLineKind.DurationDiscount, -30.5m),
            (QuoteLineKind.Extra, 50m),
            (QuoteLineKind.Extra, 40m),
            (QuoteLineKind.DailyContribution, 11.9m),
        ]);
        quote.DailyRates.Select(r => r.Period).ShouldBe(
            [RatePeriodKindInfo.MidSeason, RatePeriodKindInfo.MidSeason, RatePeriodKindInfo.MidSeason, RatePeriodKindInfo.HighSeason, RatePeriodKindInfo.HighSeason]);
    }

    [Fact]
    public async Task Drafts_and_archived_plans_never_price_a_real_quote()
    {
        _world.Draft();

        (await _world.PricingService().QuoteAsync(ReferenceRequest(), Ct)).Error.Code.ShouldBe(PricingErrorCodes.NoPublishedPlan);

        var published = _world.PublishDefault(mid: 120m);
        var second = _world.Draft(mid: 130m);
        _world.Publish(second);

        published.Status.ShouldBe(RatePlanStatus.Archived);
        (await QuoteAsync(ReferenceRequest())).ShouldSatisfyAllConditions(
            q => q.RatePlanId.ShouldBe(second.Id),
            q => q.RatePlanVersion.ShouldBe(2),
            q => q.DailyRates[0].Price.ShouldBe(Money.Tnd(130m)));
    }

    [Fact]
    public async Task A_channel_without_rule_is_priced_at_parity()
    {
        _world.PublishDefault();

        var quote = await QuoteAsync(ReferenceRequest(SalesChannel.Phone));

        quote.TotalInclVat.ShouldBe(Money.Tnd(681.400m));
        quote.Inputs.ChannelPricingMode.ShouldBe(ChannelPricingModeInfo.Parity);
    }

    [Fact]
    public async Task A_markup_rule_raises_the_daily_prices_of_the_default_grid()
    {
        var plan = _world.PublishDefault();

        var quote = await QuoteAsync(ReferenceRequest(SalesChannel.BookingCom));

        quote.DailyRates.Select(r => r.Price.Amount).ShouldBe([127m, 127m, 127m, 161m, 161m]);
        quote.RatePlanId.ShouldBe(plan.Id);
        quote.Inputs.ChannelPricingMode.ShouldBe(ChannelPricingModeInfo.Markup);
        quote.Inputs.MarkupPercent.ShouldBe(15m);
    }

    [Fact]
    public async Task An_own_grid_rule_prices_the_channel_with_its_published_plan()
    {
        var defaultPlan = _world.PublishDefault([new ChannelRuleDefinition(SalesChannel.BookingCom, ChannelPricingMode.OwnGrid)]);

        (await _world.PricingService().QuoteAsync(ReferenceRequest(SalesChannel.BookingCom), Ct)).Error.Code
            .ShouldBe(PricingErrorCodes.ChannelPlanMissing);

        var channelPlan = _world.PublishChannelPlan(SalesChannel.BookingCom, mid: 125m, high: 160m);
        var quote = await QuoteAsync(ReferenceRequest(SalesChannel.BookingCom));

        quote.RatePlanId.ShouldBe(channelPlan.Id);
        quote.DailyRates.Select(r => r.Price.Amount).ShouldBe([125m, 125m, 125m, 160m, 160m]);
        quote.Inputs.ChannelPricingMode.ShouldBe(ChannelPricingModeInfo.OwnGrid);
        quote.Inputs.DefaultPlanId.ShouldBe(defaultPlan.Id);
        (await QuoteAsync(ReferenceRequest(SalesChannel.Web))).RatePlanId.ShouldBe(defaultPlan.Id);
    }

    [Fact]
    public async Task A_published_channel_plan_is_ignored_while_the_rule_is_not_own_grid()
    {
        var defaultPlan = _world.PublishDefault();
        _world.PublishChannelPlan(SalesChannel.BookingCom, mid: 125m, high: 160m);

        (await QuoteAsync(ReferenceRequest(SalesChannel.BookingCom))).RatePlanId.ShouldBe(defaultPlan.Id);
    }

    [Fact]
    public async Task Settings_of_the_agency_feed_the_engine()
    {
        _world.PublishDefault();
        _world.Settings.Set(WellKnownSettings.VatRate, 0.07m);
        _world.Settings.Set(WellKnownSettings.DailyContributionExclVat, Money.Tnd(3m));
        _world.Settings.Set(WellKnownSettings.LateReturnTolerance, TimeSpan.FromHours(2));
        _world.Settings.Set(PricingSettings.YoungDriverAgeBelow, 30);
        _world.Settings.Set(PricingSettings.AgencyOpeningHours, new OpeningHours(new TimeOnly(9, 0), new TimeOnly(18, 0)));

        var quote = await QuoteAsync(ReferenceRequest() with
        {
            ReturnAt = Tunis(2027, 7, 3, 11, 30),
            DriverBirthDates = [new DateOnly(2000, 1, 1)],
        });

        quote.Days.ShouldBe(5);
        quote.Lines.ShouldAllBe(l => l.VatRate == 0.07m);
        quote.Lines.Single(l => l.Kind == QuoteLineKind.DailyContribution).ExclVat.ShouldBe(Money.Tnd(15m));
        quote.Lines.Single(l => l.Kind == QuoteLineKind.YoungDriver).InclVat.ShouldBe(Money.Tnd(75m));
        quote.Inputs.ShouldSatisfyAllConditions(
            i => i.VatRate.ShouldBe(0.07m),
            i => i.LateReturnTolerance.ShouldBe(TimeSpan.FromHours(2)),
            i => i.YoungDriverAgeBelow.ShouldBe(30),
            i => i.OpensAt.ShouldBe(new TimeOnly(9, 0)),
            i => i.ClosesAt.ShouldBe(new TimeOnly(18, 0)));
        _world.Settings.Reads.ShouldAllBe(r => r.AgencyId == AgencyId);
        _world.Settings.Reads.Select(r => r.Key).ShouldBe(
            [
                WellKnownSettings.VatRate.Key,
                WellKnownSettings.DailyContributionExclVat.Key,
                WellKnownSettings.LateReturnTolerance.Key,
                PricingSettings.RoundPublicPricesToDinar.Key,
                PricingSettings.YoungDriverAgeBelow.Key,
                PricingSettings.AgencyOpeningHours.Key,
            ],
            ignoreOrder: true);
    }

    [Fact]
    public async Task Dinar_rounding_can_be_switched_off()
    {
        _world.PublishDefault();
        _world.Settings.Set(PricingSettings.RoundPublicPricesToDinar, false);

        var quote = await QuoteAsync(ReferenceRequest(SalesChannel.BookingCom));

        quote.DailyRates[0].Price.ShouldBe(Money.Tnd(126.5m));
    }

    [Fact]
    public async Task The_booking_instant_comes_from_the_time_provider()
    {
        var world = new PricingWorld(new DateTimeOffset(2027, 5, 1, 9, 0, 0, TimeSpan.Zero));
        world.PublishDefault();

        var early = (await world.PricingService().QuoteAsync(ReferenceRequest(), Ct)).Value;
        world.Clock.SetUtcNow(new DateTimeOffset(2027, 6, 27, 8, 0, 0, TimeSpan.Zero));
        var late = (await world.PricingService().QuoteAsync(ReferenceRequest(), Ct)).Value;

        early.AppliedPromotions.ShouldHaveSingleItem().Kind.ShouldBe(PromotionKindInfo.EarlyBooking);
        early.Inputs.QuotedAt.ShouldBe(new DateTimeOffset(2027, 5, 1, 9, 0, 0, TimeSpan.Zero));
        late.AppliedPromotions.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_category_must_exist_and_be_active()
    {
        _world.PublishDefault();
        var inactive = _world.Fleet.AddCategory(Guid.CreateVersion7(), AgencyId, "MCMR", 500m, 250m, active: false);

        (await _world.PricingService().QuoteAsync(ReferenceRequest() with { CategoryId = Guid.CreateVersion7() }, Ct)).Error
            .ShouldBe(PricingErrors.CategoryNotFound);
        (await _world.PricingService().QuoteAsync(ReferenceRequest() with { CategoryId = inactive.Id }, Ct)).Error
            .ShouldBe(PricingErrors.CategoryNotFound);
    }

    [Fact]
    public async Task Locations_must_be_active_locations_of_the_agency()
    {
        _world.PublishDefault();
        var elsewhere = new LocationInfo(Guid.CreateVersion7(), OtherAgencyId, "SOU", "Sousse", LocationKindInfo.AgencyOffice, null);
        _world.Referential.Locations.Add(elsewhere);

        (await _world.PricingService().QuoteAsync(ReferenceRequest() with { ReturnLocationId = elsewhere.Id }, Ct)).Error
            .ShouldBe(PricingErrors.LocationNotFound);
        (await _world.PricingService().QuoteAsync(ReferenceRequest() with { PickupLocationId = Guid.CreateVersion7() }, Ct)).Error
            .ShouldBe(PricingErrors.LocationNotFound);
        (await QuoteAsync(ReferenceRequest() with { ReturnLocationId = Airport })).Inputs.ReturnLocationId.ShouldBe(Airport);
    }

    [Fact]
    public async Task The_return_must_follow_the_pickup()
    {
        _world.PublishDefault();

        (await _world.PricingService().QuoteAsync(ReferenceRequest() with { ReturnAt = Tunis(2027, 6, 28) }, Ct)).Error.Code
            .ShouldBe(PricingErrorCodes.PeriodInvalid);
    }

    [Fact]
    public async Task A_guaranteed_vehicle_adds_its_supplement_when_it_offers_one()
    {
        _world.PublishDefault();
        var offered = _world.Fleet.AddVehicle(Ecmr, supplement: 15m);
        var notOffered = _world.Fleet.AddVehicle(Ecmr, supplement: null);
        var sold = _world.Fleet.AddVehicle(Ecmr, supplement: 15m, status: VehicleStatusInfo.Sold);
        var otherCategory = _world.Fleet.AddCategory(Guid.CreateVersion7(), AgencyId, "CDMR", 800m, 400m);
        var elsewhere = _world.Fleet.AddVehicle(otherCategory.Id, supplement: 20m);
        var service = _world.PricingService();

        var quote = await QuoteAsync(ReferenceRequest() with { GuaranteedVehicleId = offered.Id });

        quote.Lines.Single(l => l.Kind == QuoteLineKind.GuaranteedModel).ShouldSatisfyAllConditions(
            l => l.InclVat.ShouldBe(Money.Tnd(75m)),
            l => l.LabelArguments.ShouldBe(["Peugeot 208 (123 TU 4567)"]));
        quote.Inputs.GuaranteedVehicleId.ShouldBe(offered.Id);
        quote.Inputs.GuaranteedModelDailySupplement.ShouldBe(Money.Tnd(15m));
        (await service.QuoteAsync(ReferenceRequest() with { GuaranteedVehicleId = notOffered.Id }, Ct)).Error.ShouldBe(PricingErrors.GuaranteedModelNotOffered);
        (await service.QuoteAsync(ReferenceRequest() with { GuaranteedVehicleId = sold.Id }, Ct)).Error.ShouldBe(PricingErrors.VehicleNotFound);
        (await service.QuoteAsync(ReferenceRequest() with { GuaranteedVehicleId = Guid.CreateVersion7() }, Ct)).Error.ShouldBe(PricingErrors.VehicleNotFound);
        (await service.QuoteAsync(ReferenceRequest() with { GuaranteedVehicleId = elsewhere.Id }, Ct)).Error
            .ShouldBe(PricingErrors.GuaranteedVehicleCategoryMismatch);
    }

    [Fact]
    public async Task The_inputs_snapshot_keeps_driver_ages_not_birth_dates()
    {
        var plan = _world.PublishDefault();

        var quote = await QuoteAsync(ReferenceRequest() with
        {
            DriverBirthDates = [new DateOnly(2004, 1, 1), new DateOnly(1980, 12, 31)],
            PromoCode = " inconnu ",
            Extras = [new SelectedExtra("child_seat", 1)],
        });

        quote.Inputs.ShouldSatisfyAllConditions(
            i => i.DriverAges.ShouldBe([23, 46]),
            i => i.PromoCode.ShouldBe("INCONNU"),
            i => i.Extras.ShouldBe([new SelectedExtra("CHILD_SEAT", 1)]),
            i => i.CategoryCode.ShouldBe("ECMR"),
            i => i.CategoryDeposit.ShouldBe(Money.Tnd(600m)),
            i => i.CategoryReducedDeposit.ShouldBe(Money.Tnd(300m)),
            i => i.DefaultPlanId.ShouldBe(plan.Id),
            i => i.DefaultPlanVersion.ShouldBe(1),
            i => i.PickupAt.ShouldBe(Tunis(2027, 6, 28)),
            i => i.DailyContributionExclVat.ShouldBe(Money.Tnd(2m)));
        quote.PromoCodeOutcome.ShouldBe(PromoCodeOutcome.Unknown);
        quote.Lines.Single(l => l.Kind == QuoteLineKind.YoungDriver).Quantity.ShouldBe(5);
    }

    [Fact]
    public async Task Engine_errors_are_returned_as_they_are()
    {
        _world.PublishDefault();

        (await _world.PricingService().QuoteAsync(ReferenceRequest() with { Extras = [new SelectedExtra("OLD_GPS", 1)] }, Ct)).Error.Code
            .ShouldBe(PricingErrorCodes.ExtraUnknown);
        (await _world.PricingService().QuoteAsync(ReferenceRequest() with { AgencyId = OtherAgencyId }, Ct)).Error.Code
            .ShouldBe(PricingErrorCodes.NoPublishedPlan);
    }
}
