using CarRental.Domain.Common;
using CarRental.Domain.Pricing;
using static CarRental.Domain.Tests.Pricing.PricingTestData;

namespace CarRental.Domain.Tests.Pricing;

public sealed class RatePlanTests
{
    private static readonly DateTimeOffset Later = Now.AddHours(1);

    private static RatePlan Draft(SalesChannel? channel = null) => RatePlan.CreateDraft(AgencyId, channel, "Grille 2027", Now).Value;

    [Fact]
    public void A_new_draft_is_empty_and_raises_an_event()
    {
        var plan = RatePlan.CreateDraft(AgencyId, null, "  Grille 2027  ", Now).Value;

        plan.Name.ShouldBe("Grille 2027");
        plan.Status.ShouldBe(RatePlanStatus.Draft);
        plan.IsDefaultPlan.ShouldBeTrue();
        plan.Version.ShouldBeNull();
        plan.Revision.ShouldBe(0);
        plan.CreatedAtUtc.ShouldBe(Now);
        plan.Seasons.ShouldBeEmpty();
        plan.Prices.ShouldBeEmpty();
        plan.DomainEvents.ShouldHaveSingleItem().ShouldBe(new RatePlanCreated(plan.Id, AgencyId, null, null));
    }

    [Fact]
    public void Creation_validates_the_agency_channel_and_name()
    {
        RatePlan.CreateDraft(Guid.Empty, null, "Grille", Now).Error.ShouldBe(PricingErrors.AgencyNotFound);
        RatePlan.CreateDraft(AgencyId, (SalesChannel)99, "Grille", Now).Error.ShouldBe(PricingErrors.ChannelRuleInvalid);
        RatePlan.CreateDraft(AgencyId, null, " ", Now).Error.ShouldBe(PricingErrors.PlanNameInvalid);
        RatePlan.CreateDraft(AgencyId, null, new string('a', RatePlan.NameMaxLength + 1), Now).Error.ShouldBe(PricingErrors.PlanNameInvalid);
        RatePlan.CreateDraft(AgencyId, SalesChannel.BookingCom, "Booking.com", Now).Value.IsDefaultPlan.ShouldBeFalse();
    }

    [Fact]
    public void Every_edit_increments_the_revision_and_raises_an_event()
    {
        var plan = Draft();
        plan.DequeueDomainEvents();

        plan.Rename("Grille été", Later).IsSuccess.ShouldBeTrue();
        plan.ReplaceSeasons([new SeasonDefinition(SeasonLevel.High, new DateOnly(2027, 7, 1), new DateOnly(2027, 8, 31), 3)], Later).IsSuccess.ShouldBeTrue();

        plan.Revision.ShouldBe(2);
        plan.ModifiedAtUtc.ShouldBe(Later);
        plan.DomainEvents.ShouldBe(
        [
            new RatePlanDraftUpdated(plan.Id, RatePlanSection.Name, 1),
            new RatePlanDraftUpdated(plan.Id, RatePlanSection.Seasons, 2),
        ]);
    }

    [Fact]
    public void Seasons_are_validated_and_never_overlap()
    {
        var plan = Draft();
        var july = new SeasonDefinition(SeasonLevel.High, new DateOnly(2027, 7, 1), new DateOnly(2027, 7, 31));

        plan.ReplaceSeasons([july with { EndDate = new DateOnly(2027, 6, 30) }], Now).Error.ShouldBe(PricingErrors.SeasonInvalid);
        plan.ReplaceSeasons([july with { MinimumDays = -1 }], Now).Error.ShouldBe(PricingErrors.SeasonInvalid);
        plan.ReplaceSeasons([july with { Level = (SeasonLevel)9 }], Now).Error.ShouldBe(PricingErrors.SeasonInvalid);
        plan.ReplaceSeasons([july, july with { Level = SeasonLevel.Mid, StartDate = new DateOnly(2027, 7, 31), EndDate = new DateOnly(2027, 8, 31) }], Now)
            .Error.ShouldBe(PricingErrors.SeasonsOverlap);
        plan.Revision.ShouldBe(0);

        plan.ReplaceSeasons([july, july with { StartDate = new DateOnly(2027, 8, 1), EndDate = new DateOnly(2027, 8, 31) }], Now).IsSuccess.ShouldBeTrue();
        plan.Seasons.Count.ShouldBe(2);
        plan.FindSeason(new DateOnly(2027, 8, 15))!.Level.ShouldBe(SeasonLevel.High);
        plan.FindSeason(new DateOnly(2027, 9, 1)).ShouldBeNull();
    }

    [Fact]
    public void Editing_a_season_with_its_id_keeps_the_entity()
    {
        var plan = Draft();
        plan.ReplaceSeasons([new SeasonDefinition(SeasonLevel.High, new DateOnly(2027, 7, 1), new DateOnly(2027, 8, 31))], Now);
        var season = plan.Seasons.Single();

        plan.ReplaceSeasons([new SeasonDefinition(SeasonLevel.High, new DateOnly(2027, 7, 1), new DateOnly(2027, 9, 15), 3, season.Id)], Now);

        plan.Seasons.ShouldHaveSingleItem().ShouldBeSameAs(season);
        season.EndDate.ShouldBe(new DateOnly(2027, 9, 15));
        season.MinimumDays.ShouldBe(3);
    }

    [Fact]
    public void Overlapping_events_need_different_priorities()
    {
        var plan = Draft();
        var aid = new PriorityEventDefinition("Aïd", new DateOnly(2027, 5, 16), new DateOnly(2027, 5, 18));

        plan.ReplaceEvents([aid, aid with { Name = "Autre", StartDate = new DateOnly(2027, 5, 18), EndDate = new DateOnly(2027, 5, 20) }], Now)
            .Error.ShouldBe(PricingErrors.EventPriorityConflict);
        plan.ReplaceEvents([aid with { Name = " " }], Now).Error.ShouldBe(PricingErrors.EventInvalid);
        plan.ReplaceEvents([aid with { Priority = 0 }], Now).Error.ShouldBe(PricingErrors.EventInvalid);
        plan.ReplaceEvents([aid with { EndDate = new DateOnly(2027, 5, 15) }], Now).Error.ShouldBe(PricingErrors.EventInvalid);

        plan.ReplaceEvents([aid, aid with { Name = "Autre", StartDate = new DateOnly(2027, 5, 18), EndDate = new DateOnly(2027, 5, 20), Priority = 2 }], Now)
            .IsSuccess.ShouldBeTrue();
        plan.FindEvents(new DateOnly(2027, 5, 18)).Select(e => e.Name).ShouldBe(["Autre", "Aïd"]);
    }

    [Fact]
    public void Removing_an_event_removes_its_prices_and_keeping_its_id_keeps_them()
    {
        var plan = Draft();
        plan.ReplaceEvents([new PriorityEventDefinition("Aïd", new DateOnly(2027, 5, 16), new DateOnly(2027, 5, 18))], Now);
        var aid = plan.Events.Single();
        plan.ReplacePrices(
            [
                new CategoryPriceDefinition(Ecmr, RatePeriodKey.Base, Money.Tnd(100m)),
                new CategoryPriceDefinition(Ecmr, RatePeriodKey.ForEvent(aid.Id), Money.Tnd(150m)),
            ],
            Now).IsSuccess.ShouldBeTrue();

        plan.ReplaceEvents([new PriorityEventDefinition("Aïd el-Kébir", aid.StartDate, aid.EndDate, 1, 0, aid.Id)], Now);
        plan.FindPrice(Ecmr, RatePeriodKey.ForEvent(aid.Id)).ShouldBe(Money.Tnd(150m));

        plan.ReplaceEvents([], Now);
        plan.Prices.ShouldHaveSingleItem().Period.ShouldBe(RatePeriodKey.Base);
    }

    [Fact]
    public void Prices_are_positive_unique_per_period_and_need_a_base_price()
    {
        var plan = Draft();
        var basePrice = new CategoryPriceDefinition(Ecmr, RatePeriodKey.Base, Money.Tnd(100m));
        var high = new CategoryPriceDefinition(Ecmr, RatePeriodKey.ForSeason(SeasonLevel.High), Money.Tnd(140m));

        plan.ReplacePrices([basePrice with { DailyPrice = Money.Zero }], Now).Error.ShouldBe(PricingErrors.PriceInvalid);
        plan.ReplacePrices([basePrice with { CategoryId = Guid.Empty }], Now).Error.ShouldBe(PricingErrors.PriceInvalid);
        plan.ReplacePrices([basePrice with { DailyPrice = Money.Of(100m, Currency.Eur) }], Now).Error.ShouldBe(PricingErrors.PriceInvalid);
        plan.ReplacePrices([basePrice, new CategoryPriceDefinition(Ecmr, RatePeriodKey.ForEvent(Guid.CreateVersion7()), Money.Tnd(1m))], Now)
            .Error.ShouldBe(PricingErrors.PriceInvalid);
        plan.ReplacePrices([basePrice, basePrice], Now).Error.ShouldBe(PricingErrors.PriceDuplicate);
        plan.ReplacePrices([high], Now).Error.ShouldBe(PricingErrors.BasePriceMissing);

        plan.ReplacePrices([basePrice, high], Now).IsSuccess.ShouldBeTrue();
        plan.FindPrice(Ecmr, RatePeriodKey.ForSeason(SeasonLevel.High)).ShouldBe(Money.Tnd(140m));
        plan.FindPrice(Ecmr, RatePeriodKey.ForSeason(SeasonLevel.Low)).ShouldBeNull();
        plan.PricedCategoryIds.ShouldBe([Ecmr]);
    }

    [Fact]
    public void Updating_a_price_keeps_its_cell()
    {
        var plan = Draft();
        plan.ReplacePrices([new CategoryPriceDefinition(Ecmr, RatePeriodKey.Base, Money.Tnd(100m))], Now);
        var cell = plan.Prices.Single();

        plan.ReplacePrices([new CategoryPriceDefinition(Ecmr, RatePeriodKey.Base, Money.Tnd(105m))], Now);

        plan.Prices.ShouldHaveSingleItem().ShouldBeSameAs(cell);
        cell.DailyPrice.ShouldBe(Money.Tnd(105m));
    }

    [Fact]
    public void Tiers_start_at_day_one_and_strictly_increase()
    {
        var plan = Draft();

        plan.ReplaceTiers([new DurationTierDefinition(3, 5m)], [], Now).Error.ShouldBe(PricingErrors.TierInvalid);
        plan.ReplaceTiers([new DurationTierDefinition(1, 0m), new DurationTierDefinition(1, 5m)], [], Now).Error.ShouldBe(PricingErrors.TierInvalid);
        plan.ReplaceTiers([new DurationTierDefinition(1, 100m)], [], Now).Error.ShouldBe(PricingErrors.TierInvalid);
        plan.ReplaceTiers([new DurationTierDefinition(1, -1m)], [], Now).Error.ShouldBe(PricingErrors.TierInvalid);
        plan.ReplaceTiers([new DurationTierDefinition(1, 5.555m)], [], Now).Error.ShouldBe(PricingErrors.TierInvalid);
        plan.ReplaceTiers([new DurationTierDefinition(1, 0m)], [new TierCategoryDiscountDefinition(3, Ecmr, 5m)], Now).Error
            .ShouldBe(PricingErrors.TierOverrideInvalid);
        plan.ReplaceTiers(
            [new DurationTierDefinition(1, 0m)],
            [new TierCategoryDiscountDefinition(1, Ecmr, 5m), new TierCategoryDiscountDefinition(1, Ecmr, 6m)],
            Now).Error.ShouldBe(PricingErrors.TierOverrideInvalid);

        plan.ReplaceTiers(
            [new DurationTierDefinition(7, 10m), new DurationTierDefinition(1, 0m), new DurationTierDefinition(3, 5m)],
            [new TierCategoryDiscountDefinition(3, Ecmr, 7.5m)],
            Now).IsSuccess.ShouldBeTrue();
        plan.Tiers.Select(t => t.MinimumDays).ShouldBe([1, 3, 7]);
        plan.FindTier(6)!.MinimumDays.ShouldBe(3);
        plan.NextTierStart(plan.FindTier(6)!).ShouldBe(7);
        plan.NextTierStart(plan.FindTier(40)!).ShouldBeNull();
        plan.TierDiscountPercent(plan.FindTier(4)!, Ecmr).ShouldBe(7.5m);
        plan.TierDiscountPercent(plan.FindTier(4)!, Mcmr).ShouldBe(5m);
        plan.ReplaceTiers([], [], Now).IsSuccess.ShouldBeTrue();
        plan.FindTier(5).ShouldBeNull();
    }

    [Fact]
    public void Extras_are_validated()
    {
        var plan = Draft();
        var seat = new ExtraDefinition("CHILD_SEAT", "Siège enfant", ExtraKind.Optional, ExtraPricingMode.PerDay, Money.Tnd(8m), Money.Tnd(50m));
        var delivery = new ExtraDefinition("DELIVERY_TUN", "Aéroport", ExtraKind.LocationDelivery, ExtraPricingMode.PerRental, Money.Zero, LocationId: Airport);

        plan.ReplaceExtras([seat with { Code = "x" }], Now).Error.ShouldBe(PricingErrors.ExtraInvalid);
        plan.ReplaceExtras([seat with { Code = "1SEAT" }], Now).Error.ShouldBe(PricingErrors.ExtraInvalid);
        plan.ReplaceExtras([seat with { Name = "" }], Now).Error.ShouldBe(PricingErrors.ExtraInvalid);
        plan.ReplaceExtras([seat with { Price = Money.Tnd(-1m) }], Now).Error.ShouldBe(PricingErrors.ExtraInvalid);
        plan.ReplaceExtras([seat with { Mode = ExtraPricingMode.PerRental }], Now).Error.ShouldBe(PricingErrors.ExtraInvalid);
        plan.ReplaceExtras([seat with { CapPerUnit = Money.Zero }], Now).Error.ShouldBe(PricingErrors.ExtraInvalid);
        plan.ReplaceExtras([seat with { LocationId = Airport }], Now).Error.ShouldBe(PricingErrors.ExtraInvalid);
        plan.ReplaceExtras([delivery with { LocationId = null }], Now).Error.ShouldBe(PricingErrors.ExtraInvalid);
        plan.ReplaceExtras([delivery with { Mode = ExtraPricingMode.PerDay }], Now).Error.ShouldBe(PricingErrors.ExtraInvalid);
        plan.ReplaceExtras([seat, seat with { Code = "child_seat" }], Now).Error.ShouldBe(PricingErrors.ExtraCodeDuplicate);
        plan.ReplaceExtras([delivery, delivery with { Code = "DELIVERY_TUN2" }], Now).Error.ShouldBe(PricingErrors.ExtraKindDuplicate);

        var youngDriver = new ExtraDefinition("YOUNG_DRIVER", "Jeune conducteur", ExtraKind.YoungDriver, ExtraPricingMode.PerDay, Money.Tnd(15m));
        plan.ReplaceExtras([youngDriver, youngDriver with { Code = "YOUNG2" }], Now).Error.ShouldBe(PricingErrors.ExtraKindDuplicate);

        plan.ReplaceExtras([seat with { Code = " child_seat " }, delivery, youngDriver, youngDriver with { Code = "YOUNG2", IsActive = false }], Now)
            .IsSuccess.ShouldBeTrue();
        plan.Extras.Select(e => (e.Code, e.Position)).ShouldBe([("CHILD_SEAT", 0), ("DELIVERY_TUN", 1), ("YOUNG_DRIVER", 2), ("YOUNG2", 3)]);
        plan.Extras[0].IsSelectable.ShouldBeTrue();
        plan.Extras[1].IsSelectable.ShouldBeFalse();
    }

    [Fact]
    public void Editing_extras_matches_existing_ones_by_id_or_code()
    {
        var plan = Draft();
        var seat = new ExtraDefinition("CHILD_SEAT", "Siège enfant", ExtraKind.Optional, ExtraPricingMode.PerDay, Money.Tnd(8m));
        var gps = new ExtraDefinition("GPS", "GPS", ExtraKind.Optional, ExtraPricingMode.PerDay, Money.Tnd(5m));
        plan.ReplaceExtras([seat, gps], Now);
        var (seatEntity, gpsEntity) = (plan.Extras[0], plan.Extras[1]);

        plan.ReplaceExtras([gps with { Price = Money.Tnd(6m) }, seat with { Code = "BABY_SEAT", Id = seatEntity.Id }], Now).IsSuccess.ShouldBeTrue();

        plan.Extras.ShouldBe([gpsEntity, seatEntity]);
        gpsEntity.Price.ShouldBe(Money.Tnd(6m));
        seatEntity.Code.ShouldBe("BABY_SEAT");
    }

    [Fact]
    public void Promotions_are_validated()
    {
        var plan = Draft();
        var code = new PromotionDefinition("Bienvenue", PromotionKind.Code, PromotionDiscountType.Percentage, DiscountPercent: 10m, Code: "BIENVENUE10");
        var early = new PromotionDefinition("Anticipée", PromotionKind.EarlyBooking, PromotionDiscountType.Percentage, DiscountPercent: 5m, MinimumDays: 30);

        plan.ReplacePromotions([code with { Code = "AB" }], Now).Error.ShouldBe(PricingErrors.PromotionInvalid);
        plan.ReplacePromotions([code with { Code = "BIEN VENUE" }], Now).Error.ShouldBe(PricingErrors.PromotionInvalid);
        plan.ReplacePromotions([code with { MinimumDays = 3 }], Now).Error.ShouldBe(PricingErrors.PromotionInvalid);
        plan.ReplacePromotions([code with { DiscountPercent = 0m }], Now).Error.ShouldBe(PricingErrors.PromotionInvalid);
        plan.ReplacePromotions([code with { DiscountPercent = 101m }], Now).Error.ShouldBe(PricingErrors.PromotionInvalid);
        plan.ReplacePromotions([code with { DiscountAmount = Money.Tnd(5m) }], Now).Error.ShouldBe(PricingErrors.PromotionInvalid);
        plan.ReplacePromotions([code with { DiscountType = PromotionDiscountType.FixedAmount, DiscountPercent = null }], Now).Error
            .ShouldBe(PricingErrors.PromotionInvalid);
        plan.ReplacePromotions([early with { MinimumDays = null }], Now).Error.ShouldBe(PricingErrors.PromotionInvalid);
        plan.ReplacePromotions([early with { Code = "EARLY" }], Now).Error.ShouldBe(PricingErrors.PromotionInvalid);
        plan.ReplacePromotions([early with { BookableFrom = new DateOnly(2027, 2, 1), BookableUntil = new DateOnly(2027, 1, 1) }], Now).Error
            .ShouldBe(PricingErrors.PromotionInvalid);
        plan.ReplacePromotions([early with { CategoryIds = [Guid.Empty] }], Now).Error.ShouldBe(PricingErrors.PromotionInvalid);
        plan.ReplacePromotions([early with { Channels = [(SalesChannel)42] }], Now).Error.ShouldBe(PricingErrors.PromotionInvalid);
        plan.ReplacePromotions([code, code with { Name = "Doublon", Code = "bienvenue10" }], Now).Error.ShouldBe(PricingErrors.PromotionCodeDuplicate);

        plan.ReplacePromotions([code with { Code = " bienvenue10 " }, early with { Channels = [SalesChannel.Web, SalesChannel.Web], CategoryIds = [Ecmr] }], Now)
            .IsSuccess.ShouldBeTrue();
        plan.Promotions[0].Code.ShouldBe("BIENVENUE10");
        plan.Promotions[1].Channels.ShouldBe([SalesChannel.Web]);
        plan.Promotions[1].CategoryIds.ShouldBe([Ecmr]);
        plan.Promotions[1].Code.ShouldBeNull();
    }

    [Fact]
    public void Channel_rules_belong_to_the_default_plan()
    {
        var plan = Draft();

        plan.ReplaceChannelRules([new ChannelRuleDefinition(SalesChannel.BookingCom, ChannelPricingMode.Markup)], Now).Error
            .ShouldBe(PricingErrors.ChannelRuleInvalid);
        plan.ReplaceChannelRules([new ChannelRuleDefinition(SalesChannel.BookingCom, ChannelPricingMode.Markup, 0m)], Now).Error
            .ShouldBe(PricingErrors.ChannelRuleInvalid);
        plan.ReplaceChannelRules([new ChannelRuleDefinition(SalesChannel.BookingCom, ChannelPricingMode.Parity, 5m)], Now).Error
            .ShouldBe(PricingErrors.ChannelRuleInvalid);
        plan.ReplaceChannelRules(
            [new ChannelRuleDefinition(SalesChannel.BookingCom, ChannelPricingMode.Parity), new ChannelRuleDefinition(SalesChannel.BookingCom, ChannelPricingMode.OwnGrid)],
            Now).Error.ShouldBe(PricingErrors.ChannelRuleInvalid);

        plan.ReplaceChannelRules([new ChannelRuleDefinition(SalesChannel.BookingCom, ChannelPricingMode.Markup, 15m)], Now).IsSuccess.ShouldBeTrue();
        ChannelPricing.From(plan.FindChannelRule(SalesChannel.BookingCom)).ShouldBe(ChannelPricing.Markup(15m));
        ChannelPricing.From(plan.FindChannelRule(SalesChannel.Web)).ShouldBe(ChannelPricing.Parity);

        Draft(SalesChannel.BookingCom).ReplaceChannelRules([new ChannelRuleDefinition(SalesChannel.Web, ChannelPricingMode.Parity)], Now).Error
            .ShouldBe(PricingErrors.ChannelRulesOnChannelPlan);
        Draft(SalesChannel.BookingCom).ReplaceChannelRules([], Now).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void Validation_blocks_a_plan_without_prices_and_warns_about_gaps_and_unpriced_events()
    {
        var plan = Draft();

        plan.Validate().ShouldContain(i => i.Code == PlanValidationCodes.NoPrice && i.IsBlocking);
        plan.Validate().ShouldContain(i => i.Code == PlanValidationCodes.NoSeason && !i.IsBlocking);

        plan.ReplaceSeasons(
            [
                new SeasonDefinition(SeasonLevel.Mid, new DateOnly(2027, 4, 1), new DateOnly(2027, 6, 30)),
                new SeasonDefinition(SeasonLevel.High, new DateOnly(2027, 7, 3), new DateOnly(2027, 8, 31)),
            ],
            Now);
        plan.ReplaceEvents([new PriorityEventDefinition("Aïd", new DateOnly(2027, 5, 16), new DateOnly(2027, 5, 18))], Now);
        plan.ReplacePrices([new CategoryPriceDefinition(Ecmr, RatePeriodKey.Base, Money.Tnd(100m))], Now);

        var issues = plan.Validate();
        issues.ShouldNotContain(i => i.IsBlocking);
        issues.ShouldContain(new PlanValidationIssue(PlanValidationCodes.SeasonGap, false, ["2027-07-01", "2027-07-02"]), new IssueComparer());
        issues.ShouldContain(new PlanValidationIssue(PlanValidationCodes.EventWithoutPrice, false, ["Aïd"]), new IssueComparer());
    }

    [Fact]
    public void Publishing_gives_the_next_version_and_archives_the_published_plan()
    {
        var first = ReferencePlan().Build();
        first.Publish(currentlyPublished: null, lastVersion: 0, Now).IsSuccess.ShouldBeTrue();
        first.Status.ShouldBe(RatePlanStatus.Published);
        first.Version.ShouldBe(1);
        first.PublishedAtUtc.ShouldBe(Now);

        var second = first.CopyAsDraft(Later).Value;
        second.Publish(first, lastVersion: 1, Later).IsSuccess.ShouldBeTrue();

        second.Version.ShouldBe(2);
        first.Status.ShouldBe(RatePlanStatus.Archived);
        first.ArchivedAtUtc.ShouldBe(Later);
        first.DomainEvents.ShouldContain(new RatePlanArchived(first.Id, AgencyId, null, 1));
        second.DomainEvents.ShouldContain(new RatePlanPublished(second.Id, AgencyId, null, 2, first.Id));
    }

    [Fact]
    public void Only_a_valid_draft_can_be_published_and_a_published_plan_is_immutable()
    {
        Draft().Publish(null, 0, Now).Error.ShouldBe(PricingErrors.PlanInvalid);

        var plan = ReferencePlan().Build();
        plan.Publish(null, 0, Now);

        plan.Publish(null, 1, Now).Error.ShouldBe(PricingErrors.PlanNotEditable);
        plan.Rename("Autre", Now).Error.ShouldBe(PricingErrors.PlanNotEditable);
        plan.ReplaceSeasons([], Now).Error.ShouldBe(PricingErrors.PlanNotEditable);
        plan.ReplaceEvents([], Now).Error.ShouldBe(PricingErrors.PlanNotEditable);
        plan.ReplacePrices([], Now).Error.ShouldBe(PricingErrors.PlanNotEditable);
        plan.ReplaceTiers([], [], Now).Error.ShouldBe(PricingErrors.PlanNotEditable);
        plan.ReplaceExtras([], Now).Error.ShouldBe(PricingErrors.PlanNotEditable);
        plan.ReplacePromotions([], Now).Error.ShouldBe(PricingErrors.PlanNotEditable);
        plan.ReplaceChannelRules([], Now).Error.ShouldBe(PricingErrors.PlanNotEditable);
    }

    [Fact]
    public void Replacing_a_plan_of_another_agency_or_channel_is_a_bug()
    {
        var published = ReferencePlan().Build();
        published.Publish(null, 0, Now);
        var channelDraft = new PlanBuilder().ForChannel(SalesChannel.BookingCom).Prices(Ecmr, 100m).Build();

        Should.Throw<InvalidOperationException>(() => channelDraft.Publish(published, 1, Now));
    }

    [Fact]
    public void Archiving_withdraws_a_draft_or_a_published_plan_once()
    {
        var draft = Draft();

        draft.Archive(Later).IsSuccess.ShouldBeTrue();
        draft.Status.ShouldBe(RatePlanStatus.Archived);
        draft.Archive(Later).Error.ShouldBe(PricingErrors.PlanAlreadyArchived);
    }

    [Fact]
    public void A_copy_is_a_new_draft_with_the_same_content_and_new_ids()
    {
        var plan = ReferencePlan()
            .Event("Aïd", new DateOnly(2027, 5, 16), new DateOnly(2027, 5, 18), minimumDays: 2)
            .EventPrice(Ecmr, "Aïd", 150m)
            .TierDiscount(3, Ecmr, 7m)
            .Promotion(new PromotionDefinition("Bienvenue", PromotionKind.Code, PromotionDiscountType.Percentage, DiscountPercent: 10m, Code: "BIENVENUE10"))
            .ChannelRule(SalesChannel.BookingCom, ChannelPricingMode.Markup, 15m)
            .Build();
        plan.Publish(null, 0, Now);

        var copy = plan.CopyAsDraft(Later, "Grille 2028").Value;

        copy.Id.ShouldNotBe(plan.Id);
        copy.Status.ShouldBe(RatePlanStatus.Draft);
        copy.Version.ShouldBeNull();
        copy.BasedOnPlanId.ShouldBe(plan.Id);
        copy.Name.ShouldBe("Grille 2028");
        copy.Seasons.Select(s => (s.Level, s.StartDate, s.EndDate)).ShouldBe(plan.Seasons.Select(s => (s.Level, s.StartDate, s.EndDate)));
        copy.Seasons.Select(s => s.Id).ShouldNotContain(plan.Seasons[0].Id);
        var copiedEvent = copy.Events.Single();
        copiedEvent.Id.ShouldNotBe(plan.Events.Single().Id);
        copy.FindPrice(Ecmr, RatePeriodKey.ForEvent(copiedEvent.Id)).ShouldBe(Money.Tnd(150m));
        copy.Prices.Count.ShouldBe(plan.Prices.Count);
        copy.TierDiscountPercent(copy.FindTier(3)!, Ecmr).ShouldBe(7m);
        copy.Extras.Select(e => e.Code).ShouldBe(plan.Extras.Select(e => e.Code));
        copy.Promotions.Single().Code.ShouldBe("BIENVENUE10");
        copy.ChannelRules.Single().MarkupPercent.ShouldBe(15m);
        copy.DomainEvents.ShouldHaveSingleItem().ShouldBe(new RatePlanCreated(copy.Id, AgencyId, null, plan.Id));
        Quote(copy, ReferenceRequest()).TotalInclVat.ShouldBe(Quote(plan, ReferenceRequest()).TotalInclVat);
    }

    [Fact]
    public void A_channel_draft_copies_the_default_plan_without_its_channel_rules()
    {
        var plan = ReferencePlan().ChannelRule(SalesChannel.BookingCom, ChannelPricingMode.OwnGrid).Build();

        var channelDraft = plan.CopyAsChannelDraft(SalesChannel.BookingCom, Later).Value;

        channelDraft.Channel.ShouldBe(SalesChannel.BookingCom);
        channelDraft.ChannelRules.ShouldBeEmpty();
        channelDraft.Prices.Count.ShouldBe(plan.Prices.Count);
        channelDraft.CopyAsChannelDraft(SalesChannel.Web, Later).Error.ShouldBe(PricingErrors.ChannelRuleInvalid);
    }

    private sealed class IssueComparer : IEqualityComparer<PlanValidationIssue>
    {
        public bool Equals(PlanValidationIssue? x, PlanValidationIssue? y) =>
            x is not null && y is not null && x.Code == y.Code && x.IsBlocking == y.IsBlocking && x.Arguments.SequenceEqual(y.Arguments);

        public int GetHashCode(PlanValidationIssue obj) => obj.Code.GetHashCode(StringComparison.Ordinal);
    }
}
