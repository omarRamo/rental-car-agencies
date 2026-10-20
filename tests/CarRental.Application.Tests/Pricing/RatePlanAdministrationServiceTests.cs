using CarRental.Application.Identity.Contracts;
using CarRental.Application.Pricing.Administration;
using CarRental.Application.Pricing.Contracts;
using CarRental.Domain.Common;
using CarRental.Domain.Pricing;
using static CarRental.Application.Tests.Pricing.PricingWorld;

namespace CarRental.Application.Tests.Pricing;

public sealed class RatePlanAdministrationServiceTests
{
    private readonly PricingWorld _world = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private RatePlanAdministrationService AsManager() => _world.Administration(Roles.Manager);

    [Fact]
    public async Task Without_pricing_read_nothing_is_visible()
    {
        var plan = _world.PublishDefault();
        var agent = _world.Administration(Roles.Agent);

        (await agent.ListAsync(AgencyId, includeArchived: true, Ct)).Error.ShouldBe(PricingErrors.Forbidden);
        (await agent.GetAsync(plan.Id, Ct)).Error.ShouldBe(PricingErrors.Forbidden);
        (await agent.GetHistoryAsync(AgencyId, null, Ct)).Error.ShouldBe(PricingErrors.Forbidden);
        (await agent.ValidateAsync(plan.Id, Ct)).Error.ShouldBe(PricingErrors.Forbidden);
        (await agent.SimulateAsync(new SimulationRequest(ReferenceRequest()), Ct)).Error.ShouldBe(PricingErrors.Forbidden);
    }

    [Fact]
    public async Task Pricing_read_allows_reading_and_simulating_but_no_change()
    {
        var plan = _world.PublishDefault();
        var draft = _world.Draft();
        var accountant = _world.Administration(Roles.Accountant);

        (await accountant.ListAsync(AgencyId, includeArchived: false, Ct)).Value.Count.ShouldBe(2);
        (await accountant.GetAsync(plan.Id, Ct)).IsSuccess.ShouldBeTrue();
        (await accountant.SimulateAsync(new SimulationRequest(ReferenceRequest()), Ct)).IsSuccess.ShouldBeTrue();

        (await accountant.CreateDraftAsync(new CreateDraftRequest(AgencyId, SalesChannel.BookingCom), Ct)).Error.ShouldBe(PricingErrors.Forbidden);
        (await accountant.RenameAsync(draft.Id, draft.Revision, "Nouveau nom", Ct)).Error.ShouldBe(PricingErrors.Forbidden);
        (await accountant.UpdateSeasonsAsync(draft.Id, draft.Revision, [], Ct)).Error.ShouldBe(PricingErrors.Forbidden);
        (await accountant.UpdatePricesAsync(draft.Id, draft.Revision, [], Ct)).Error.ShouldBe(PricingErrors.Forbidden);
        (await accountant.PublishAsync(draft.Id, draft.Revision, Ct)).Error.ShouldBe(PricingErrors.Forbidden);
        (await accountant.ArchiveAsync(draft.Id, Ct)).Error.ShouldBe(PricingErrors.Forbidden);
        draft.Name.ShouldBe("Grille");
        _world.UnitOfWork.Executions.ShouldBe(0);
    }

    [Fact]
    public async Task A_draft_copies_the_published_plan_and_only_one_draft_exists_per_channel()
    {
        var published = _world.PublishDefault();
        var service = AsManager();

        var created = await service.CreateDraftAsync(new CreateDraftRequest(AgencyId, null, "Grille 2028"), Ct);

        var draft = _world.Store.Plans.Single(p => p.Id == created.Value);
        draft.Status.ShouldBe(RatePlanStatus.Draft);
        draft.BasedOnPlanId.ShouldBe(published.Id);
        draft.Name.ShouldBe("Grille 2028");
        draft.Prices.Count.ShouldBe(published.Prices.Count);
        draft.ChannelRules.ShouldHaveSingleItem().Channel.ShouldBe(SalesChannel.BookingCom);
        (await service.CreateDraftAsync(new CreateDraftRequest(AgencyId, null), Ct)).Error.ShouldBe(PricingErrors.DraftAlreadyExists);
        (await service.CreateDraftAsync(new CreateDraftRequest(Guid.CreateVersion7(), null), Ct)).Error.ShouldBe(PricingErrors.AgencyNotFound);
    }

    [Fact]
    public async Task The_first_own_grid_of_a_channel_starts_from_the_default_plan()
    {
        var published = _world.PublishDefault();

        var created = await AsManager().CreateDraftAsync(new CreateDraftRequest(AgencyId, SalesChannel.BookingCom, "Grille Booking.com"), Ct);

        var draft = _world.Store.Plans.Single(p => p.Id == created.Value);
        draft.Channel.ShouldBe(SalesChannel.BookingCom);
        draft.BasedOnPlanId.ShouldBe(published.Id);
        draft.ChannelRules.ShouldBeEmpty();
        draft.PricedCategoryIds.ShouldBe([Ecmr]);
    }

    [Fact]
    public async Task Without_any_plan_a_draft_starts_empty()
    {
        var created = await AsManager().CreateDraftAsync(new CreateDraftRequest(AgencyId, null), Ct);

        var draft = _world.Store.Plans.Single(p => p.Id == created.Value);
        draft.Name.ShouldBe(RatePlanAdministrationService.DefaultPlanName);
        draft.Prices.ShouldBeEmpty();
        draft.BasedOnPlanId.ShouldBeNull();
    }

    [Fact]
    public async Task An_old_version_can_be_restored_as_a_draft()
    {
        var first = _world.PublishDefault(mid: 120m);
        _world.Publish(_world.Draft(mid: 130m));

        var created = await AsManager().CreateDraftAsync(new CreateDraftRequest(AgencyId, null, SourcePlanId: first.Id), Ct);

        var draft = _world.Store.Plans.Single(p => p.Id == created.Value);
        draft.BasedOnPlanId.ShouldBe(first.Id);
        draft.FindPrice(Ecmr, RatePeriodKey.ForSeason(SeasonLevel.Mid)).ShouldBe(Money.Tnd(120m));
        (await AsManager().CreateDraftAsync(new CreateDraftRequest(OtherAgencyId, null, SourcePlanId: first.Id), Ct)).Error
            .ShouldBe(PricingErrors.PlanNotFound);
    }

    [Fact]
    public async Task Edits_need_a_draft_and_the_revision_the_editor_read()
    {
        var published = _world.PublishDefault();
        var draft = _world.Draft();
        var service = AsManager();
        var seasons = new SeasonDefinition[] { new(SeasonLevel.High, new DateOnly(2027, 7, 1), new DateOnly(2027, 8, 31), 3) };

        var edited = await service.UpdateSeasonsAsync(draft.Id, draft.Revision, seasons, Ct);

        edited.IsSuccess.ShouldBeTrue();
        edited.Value.Seasons.ShouldHaveSingleItem().MinimumDays.ShouldBe(3);
        edited.Value.Plan.Revision.ShouldBe(draft.Revision);
        (await service.UpdateSeasonsAsync(draft.Id, draft.Revision - 1, seasons, Ct)).Error.ShouldBe(PricingErrors.PlanModifiedConcurrently);
        (await service.UpdateSeasonsAsync(published.Id, published.Revision, seasons, Ct)).Error.ShouldBe(PricingErrors.PlanNotEditable);
        (await service.UpdateSeasonsAsync(Guid.CreateVersion7(), 0, seasons, Ct)).Error.ShouldBe(PricingErrors.PlanNotFound);
        (await service.UpdateSeasonsAsync(draft.Id, draft.Revision, [seasons[0], seasons[0]], Ct)).Error.ShouldBe(PricingErrors.SeasonsOverlap);
    }

    [Fact]
    public async Task Every_section_of_a_draft_can_be_edited()
    {
        var draft = _world.Draft();
        var service = AsManager();

        var renamed = await service.RenameAsync(draft.Id, draft.Revision, "Grille hiver", Ct);
        var events = await service.UpdateEventsAsync(draft.Id, draft.Revision, [new PriorityEventDefinition("Aïd", new DateOnly(2027, 5, 16), new DateOnly(2027, 5, 18))], Ct);
        var eventId = events.Value.Events.Single().Id!.Value;
        var prices = await service.UpdatePricesAsync(
            draft.Id,
            draft.Revision,
            [new(Ecmr, RatePeriodKey.Base, Money.Tnd(95m)), new(Ecmr, RatePeriodKey.ForEvent(eventId), Money.Tnd(150m))],
            Ct);
        var tiers = await service.UpdateTiersAsync(draft.Id, draft.Revision, [new(1, 0m), new(7, 12m)], [new(7, Ecmr, 14m)], Ct);
        var extras = await service.UpdateExtrasAsync(
            draft.Id,
            draft.Revision,
            [new ExtraDefinition("GPS", "GPS", ExtraKind.Optional, ExtraPricingMode.PerDay, Money.Tnd(5m))],
            Ct);
        var promotions = await service.UpdatePromotionsAsync(draft.Id, draft.Revision, [], Ct);
        var rules = await service.UpdateChannelRulesAsync(
            draft.Id,
            draft.Revision,
            [new ChannelRuleDefinition(SalesChannel.Counter, ChannelPricingMode.Markup, 5m)],
            Ct);

        new Result[] { renamed, events, prices, tiers, extras, promotions, rules }.ShouldAllBe(r => r.IsSuccess);
        var detail = rules.Value;
        detail.Plan.Name.ShouldBe("Grille hiver");
        detail.Prices.ShouldContain(new CategoryPriceDefinition(Ecmr, RatePeriodKey.ForEvent(eventId), Money.Tnd(150m)));
        detail.Tiers.Select(t => t.MinimumDays).ShouldBe([1, 7]);
        detail.TierCategoryDiscounts.ShouldHaveSingleItem().DiscountPercent.ShouldBe(14m);
        detail.Extras.ShouldHaveSingleItem().Code.ShouldBe("GPS");
        detail.Promotions.ShouldBeEmpty();
        detail.ChannelRules.ShouldHaveSingleItem().Channel.ShouldBe(SalesChannel.Counter);
        detail.Plan.Revision.ShouldBe(draft.Revision);
        _world.UnitOfWork.Executions.ShouldBe(7);
    }

    [Fact]
    public async Task A_delivery_extra_must_target_a_location_of_the_plan_agency()
    {
        var draft = _world.Draft();
        var service = AsManager();
        ExtraDefinition Delivery(Guid? locationId) =>
            new("DELIVERY", "Livraison", ExtraKind.LocationDelivery, ExtraPricingMode.PerRental, Money.Tnd(15m), LocationId: locationId);

        (await service.UpdateExtrasAsync(draft.Id, draft.Revision, [Delivery(Guid.CreateVersion7())], Ct)).Error.ShouldBe(PricingErrors.ExtraInvalid);
        (await service.UpdateExtrasAsync(draft.Id, draft.Revision, [Delivery(PricingWorld.Airport)], Ct)).IsSuccess.ShouldBeTrue();
        (await _world.Administration(Roles.Agent).UpdateExtrasAsync(draft.Id, draft.Revision, [Delivery(Guid.CreateVersion7())], Ct)).Error.ShouldBe(PricingErrors.Forbidden);
    }

    [Fact]
    public async Task Publishing_gives_the_next_version_and_archives_the_previous_plan()
    {
        var first = _world.PublishDefault();
        var draft = _world.Draft(mid: 120m);

        var published = await AsManager().PublishAsync(draft.Id, draft.Revision, Ct);

        published.Value.Plan.Status.ShouldBe(RatePlanStatus.Published);
        published.Value.Plan.Version.ShouldBe(2);
        first.Status.ShouldBe(RatePlanStatus.Archived);
        (await AsManager().GetHistoryAsync(AgencyId, null, Ct)).Value.Select(p => (p.Status, p.Version)).ShouldBe(
            [(RatePlanStatus.Published, 2), (RatePlanStatus.Archived, (int?)1)]);
        (await _world.PricingService().QuoteAsync(ReferenceRequest(), Ct)).Value.RatePlanVersion.ShouldBe(2);
    }

    [Fact]
    public async Task A_plan_without_price_cannot_be_published()
    {
        var created = await AsManager().CreateDraftAsync(new CreateDraftRequest(AgencyId, null), Ct);
        var draft = _world.Store.Plans.Single(p => p.Id == created.Value);

        var detail = await AsManager().GetAsync(draft.Id, Ct);

        detail.Value.CanBePublished.ShouldBeFalse();
        detail.Value.Issues.ShouldContain(i => i.Code == PlanValidationCodes.NoPrice && i.IsBlocking);
        (await AsManager().PublishAsync(draft.Id, draft.Revision, Ct)).Error.ShouldBe(PricingErrors.PlanInvalid);
    }

    [Fact]
    public async Task Validation_warns_about_an_own_grid_channel_without_published_plan()
    {
        var draft = _world.Draft();
        draft.ReplaceChannelRules([new ChannelRuleDefinition(SalesChannel.BookingCom, ChannelPricingMode.OwnGrid)], _world.Clock.GetUtcNow())
            .IsSuccess.ShouldBeTrue();

        var issues = (await AsManager().ValidateAsync(draft.Id, Ct)).Value;

        issues.ShouldContain(i => i.Code == PlanValidationCodes.ChannelPlanMissing && !i.IsBlocking && i.Arguments[0] == "BookingCom");
        _world.PublishChannelPlan(SalesChannel.BookingCom, 120m, 150m);
        (await AsManager().ValidateAsync(draft.Id, Ct)).Value.ShouldNotContain(i => i.Code == PlanValidationCodes.ChannelPlanMissing);
    }

    [Fact]
    public async Task Archiving_withdraws_a_plan()
    {
        var published = _world.PublishDefault();

        (await AsManager().ArchiveAsync(published.Id, Ct)).Value.Plan.Status.ShouldBe(RatePlanStatus.Archived);
        (await AsManager().ArchiveAsync(published.Id, Ct)).Error.ShouldBe(PricingErrors.PlanAlreadyArchived);
        (await _world.PricingService().QuoteAsync(ReferenceRequest(), Ct)).Error.ShouldBe(PricingErrors.NoPublishedPlan);
    }

    [Fact]
    public async Task The_simulator_compares_a_draft_with_the_published_plan_for_the_same_request()
    {
        _world.PublishDefault();
        var draft = _world.Draft(mid: 120m, high: 150m);

        var simulation = (await AsManager().SimulateAsync(new SimulationRequest(ReferenceRequest(), ComparedPlanId: draft.Id), Ct)).Value;

        simulation.Published.Quote!.TotalInclVat.ShouldBe(Money.Tnd(681.400m));
        simulation.Compared.ShouldNotBeNull();
        simulation.Compared.Quote!.RatePlanId.ShouldBe(draft.Id);
        simulation.Compared.Quote.RatePlanVersion.ShouldBeNull();
        simulation.Compared.Quote.RentalSubtotal.ShouldBe(Money.Tnd(660m));
    }

    [Fact]
    public async Task The_simulator_chooses_the_booking_instant()
    {
        _world.PublishDefault();

        var now = (await AsManager().SimulateAsync(new SimulationRequest(ReferenceRequest()), Ct)).Value;
        var early = (await AsManager().SimulateAsync(new SimulationRequest(ReferenceRequest(), QuotedAt: Tunis(2027, 1, 10)), Ct)).Value;

        now.Published.Quote!.AppliedPromotions.ShouldBeEmpty();
        early.Published.Quote!.AppliedPromotions.ShouldHaveSingleItem().Kind.ShouldBe(PromotionKindInfo.EarlyBooking);
        now.Compared.ShouldBeNull();
    }

    [Fact]
    public async Task The_simulator_reports_errors_on_each_side()
    {
        var channelDraft = _world.Draft(SalesChannel.BookingCom, 120m, 150m);
        var foreign = _world.Draft(agencyId: OtherAgencyId);

        var noPlan = (await AsManager().SimulateAsync(new SimulationRequest(ReferenceRequest(), ComparedPlanId: Guid.CreateVersion7()), Ct)).Value;
        noPlan.Published.Error.ShouldBe(PricingErrors.NoPublishedPlan);
        noPlan.Compared!.Error.ShouldBe(PricingErrors.PlanNotFound);

        _world.PublishDefault();
        var mismatch = (await AsManager().SimulateAsync(new SimulationRequest(ReferenceRequest(SalesChannel.Web), ComparedPlanId: channelDraft.Id), Ct)).Value;
        mismatch.Compared!.Error.ShouldBe(PricingErrors.SimulationChannelMismatch);
        var otherAgency = (await AsManager().SimulateAsync(new SimulationRequest(ReferenceRequest(), ComparedPlanId: foreign.Id), Ct)).Value;
        otherAgency.Compared!.Error.ShouldBe(PricingErrors.PlanNotFound);

        var channel = (await AsManager().SimulateAsync(new SimulationRequest(ReferenceRequest(SalesChannel.BookingCom), ComparedPlanId: channelDraft.Id), Ct)).Value;
        channel.Published.Quote!.DailyRates[0].Price.ShouldBe(Money.Tnd(127m));
        channel.Compared!.Quote!.DailyRates[0].Price.ShouldBe(Money.Tnd(120m));
    }
}
