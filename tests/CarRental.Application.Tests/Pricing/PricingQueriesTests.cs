using CarRental.Application.Pricing.Contracts;
using CarRental.Domain.Common;
using CarRental.Domain.Pricing;
using static CarRental.Application.Tests.Pricing.PricingWorld;

namespace CarRental.Application.Tests.Pricing;

public sealed class PricingQueriesTests
{
    private readonly PricingWorld _world = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Extras_of_the_plan_pricing_the_channel_are_offered_with_their_rules()
    {
        _world.PublishDefault();

        var extras = await _world.Queries().GetExtrasAsync(AgencyId, SalesChannel.Web, Ct);

        extras.Select(e => e.Code).ShouldBe(["ADDITIONAL_DRIVER", "CHILD_SEAT", "YOUNG_DRIVER"]);
        extras[1].ShouldBe(new ExtraOffer(
            "CHILD_SEAT",
            "Siège enfant",
            null,
            ExtraKindInfo.Optional,
            ExtraPricingModeInfo.PerDay,
            Money.Tnd(8m),
            Money.Tnd(50m),
            null,
            IsSelectable: true));
        extras[2].IsSelectable.ShouldBeFalse();
        (await _world.Queries().GetExtrasAsync(OtherAgencyId, SalesChannel.Web, Ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task An_own_grid_channel_offers_the_extras_of_its_plan()
    {
        _world.PublishDefault([new ChannelRuleDefinition(SalesChannel.BookingCom, ChannelPricingMode.OwnGrid)]);
        var channelPlan = _world.Draft(SalesChannel.BookingCom);
        channelPlan.ReplaceExtras(
            [new ExtraDefinition("GPS", "GPS", ExtraKind.Optional, ExtraPricingMode.PerRental, Money.Tnd(20m))],
            _world.Clock.GetUtcNow()).IsSuccess.ShouldBeTrue();
        _world.Publish(channelPlan);

        var extras = await _world.Queries().GetExtrasAsync(AgencyId, SalesChannel.BookingCom, Ct);

        extras.ShouldHaveSingleItem().Code.ShouldBe("GPS");
    }

    [Fact]
    public async Task The_published_plan_summary_lists_seasons_events_prices_and_rules()
    {
        var plan = _world.PublishDefault();

        var summary = await _world.Queries().GetPublishedPlanAsync(AgencyId, cancellationToken: Ct);

        summary.ShouldNotBeNull();
        summary.Id.ShouldBe(plan.Id);
        summary.Version.ShouldBe(1);
        summary.Channel.ShouldBeNull();
        summary.Seasons.Select(s => s.Level).ShouldBe([SeasonLevelInfo.Mid, SeasonLevelInfo.High]);
        summary.CategoryRates.ShouldHaveSingleItem().ShouldSatisfyAllConditions(
            r => r.CategoryId.ShouldBe(Ecmr),
            r => r.BasePrice.ShouldBe(Money.Tnd(100m)),
            r => r.LowSeasonPrice.ShouldBeNull(),
            r => r.MidSeasonPrice.ShouldBe(Money.Tnd(110m)),
            r => r.HighSeasonPrice.ShouldBe(Money.Tnd(140m)),
            r => r.EventPrices.ShouldBeEmpty());
        summary.ChannelRules.ShouldBe([new ChannelRuleInfo(SalesChannel.BookingCom, ChannelPricingModeInfo.Markup, 15m)]);
        (await _world.Queries().GetPublishedPlanAsync(AgencyId, SalesChannel.BookingCom, Ct)).ShouldBeNull();
    }
}
