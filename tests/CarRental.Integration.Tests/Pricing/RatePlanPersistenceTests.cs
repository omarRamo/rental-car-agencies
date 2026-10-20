using CarRental.Application.Pricing.Administration;
using CarRental.Domain.Common;
using CarRental.Domain.Identity;
using CarRental.Domain.Pricing;
using CarRental.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static CarRental.Integration.Tests.Pricing.PricingScenario;

namespace CarRental.Integration.Tests.Pricing;

/// <summary>Rate plans on SQL Server: full round trip, temporal history, versions and audit.</summary>
public sealed class RatePlanPersistenceTests(IntegrationHost host)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_full_plan_round_trips_through_the_database()
    {
        var agency = await CreateAgencyAsync(host, Ct);
        var draftId = await AdministerAsync(host, s => s.CreateDraftAsync(new CreateDraftRequest(agency.AgencyId, null, "Grille complète"), Ct));
        var detail = await AdministerAsync(host, s => s.UpdateSeasonsAsync(
            draftId,
            0,
            [
                new SeasonDefinition(SeasonLevel.Low, new DateOnly(2027, 1, 1), new DateOnly(2027, 3, 31)),
                new SeasonDefinition(SeasonLevel.High, new DateOnly(2027, 7, 1), new DateOnly(2027, 8, 31), 3),
            ],
            Ct));
        detail = await AdministerAsync(host, s => s.UpdateEventsAsync(
            draftId,
            detail.Plan.Revision,
            [new PriorityEventDefinition("Aïd el-Kébir", new DateOnly(2027, 5, 16), new DateOnly(2027, 5, 18), 2, 2)],
            Ct));
        var eventId = detail.Events.Single().Id!.Value;
        detail = await AdministerAsync(host, s => s.UpdatePricesAsync(
            draftId,
            detail.Plan.Revision,
            [
                new CategoryPriceDefinition(agency.Ecmr, RatePeriodKey.Base, Money.Tnd(100.5m)),
                new CategoryPriceDefinition(agency.Ecmr, RatePeriodKey.ForSeason(SeasonLevel.Low), Money.Tnd(90m)),
                new CategoryPriceDefinition(agency.Ecmr, RatePeriodKey.ForEvent(eventId), Money.Tnd(155.250m)),
                new CategoryPriceDefinition(agency.Mcmr, RatePeriodKey.Base, Money.Tnd(80m)),
            ],
            Ct));
        detail = await AdministerAsync(host, s => s.UpdateTiersAsync(
            draftId,
            detail.Plan.Revision,
            [new(1, 0m), new(3, 5m), new(7, 12.5m)],
            [new(7, agency.Ecmr, 14.75m)],
            Ct));
        detail = await AdministerAsync(host, s => s.UpdateExtrasAsync(
            draftId,
            detail.Plan.Revision,
            [
                new ExtraDefinition("CHILD_SEAT", "Siège enfant", ExtraKind.Optional, ExtraPricingMode.PerDay, Money.Tnd(8m), Money.Tnd(50m), NameEn: "Child seat"),
                new ExtraDefinition("DELIVERY_AIRPORT", "Aéroport", ExtraKind.LocationDelivery, ExtraPricingMode.PerRental, Money.Zero, LocationId: agency.AirportId),
                new ExtraDefinition("ONE_WAY", "Aller simple", ExtraKind.OneWay, ExtraPricingMode.PerRental, Money.Tnd(60m), IsActive: false),
            ],
            Ct));
        detail = await AdministerAsync(host, s => s.UpdatePromotionsAsync(
            draftId,
            detail.Plan.Revision,
            [
                new PromotionDefinition(
                    "Été",
                    PromotionKind.Code,
                    PromotionDiscountType.FixedAmount,
                    DiscountAmount: Money.Tnd(25.5m),
                    Code: "ETE2027",
                    BookableFrom: new DateOnly(2027, 1, 1),
                    BookableUntil: new DateOnly(2027, 6, 30),
                    PickupFrom: new DateOnly(2027, 7, 1),
                    PickupUntil: new DateOnly(2027, 8, 31),
                    CategoryIds: [agency.Ecmr, agency.Mcmr],
                    Channels: [SalesChannel.Web, SalesChannel.WhatsApp]),
                new PromotionDefinition("Long séjour", PromotionKind.LongStay, PromotionDiscountType.Percentage, DiscountPercent: 7.5m, MinimumDays: 14, IsCombinable: true),
            ],
            Ct));
        detail = await AdministerAsync(host, s => s.UpdateChannelRulesAsync(
            draftId,
            detail.Plan.Revision,
            [new ChannelRuleDefinition(SalesChannel.BookingCom, ChannelPricingMode.Markup, 15.5m), new ChannelRuleDefinition(SalesChannel.Counter, ChannelPricingMode.Parity)],
            Ct));

        var reloaded = await AdministerAsync(host, s => s.GetAsync(draftId, Ct));

        reloaded.Plan.Name.ShouldBe("Grille complète");
        reloaded.Plan.Revision.ShouldBe(7);
        reloaded.Seasons.ShouldBe(detail.Seasons);
        reloaded.Events.ShouldBe(detail.Events);
        reloaded.Prices.ShouldBe(detail.Prices, ignoreOrder: true);
        reloaded.Prices.ShouldContain(new CategoryPriceDefinition(agency.Ecmr, RatePeriodKey.ForEvent(eventId), Money.Tnd(155.250m)));
        reloaded.Tiers.ShouldBe([new(1, 0m), new(3, 5m), new(7, 12.5m)]);
        reloaded.TierCategoryDiscounts.ShouldBe([new(7, agency.Ecmr, 14.75m)]);
        reloaded.Extras.Select(e => (e.Code, e.Price, e.CapPerUnit, e.LocationId, e.IsActive, e.NameEn)).ShouldBe(
        [
            ("CHILD_SEAT", Money.Tnd(8m), (Money?)Money.Tnd(50m), (Guid?)null, true, "Child seat"),
            ("DELIVERY_AIRPORT", Money.Zero, null, agency.AirportId, true, null),
            ("ONE_WAY", Money.Tnd(60m), null, null, false, null),
        ]);
        var summer = reloaded.Promotions[0];
        summer.Code.ShouldBe("ETE2027");
        summer.DiscountAmount.ShouldBe(Money.Tnd(25.5m));
        summer.CategoryIds!.ShouldBe([agency.Ecmr, agency.Mcmr], ignoreOrder: true);
        summer.Channels!.ShouldBe([SalesChannel.Web, SalesChannel.WhatsApp]);
        (summer.BookableFrom, summer.BookableUntil, summer.PickupFrom, summer.PickupUntil).ShouldBe(
            (new DateOnly(2027, 1, 1), new DateOnly(2027, 6, 30), new DateOnly(2027, 7, 1), new DateOnly(2027, 8, 31)));
        reloaded.Promotions[1].ShouldSatisfyAllConditions(
            p => p.DiscountPercent.ShouldBe(7.5m),
            p => p.IsCombinable.ShouldBeTrue(),
            p => p.Channels.ShouldBeEmpty());
        reloaded.ChannelRules.ShouldBe(
            [new ChannelRuleDefinition(SalesChannel.Counter, ChannelPricingMode.Parity), new ChannelRuleDefinition(SalesChannel.BookingCom, ChannelPricingMode.Markup, 15.5m)],
            ignoreOrder: true);
    }

    [Fact]
    public async Task Edits_keep_the_previous_rows_in_the_temporal_history_and_in_the_audit_log()
    {
        var agency = await CreateAgencyAsync(host, Ct);
        var draftId = await AdministerAsync(host, s => s.CreateDraftAsync(new CreateDraftRequest(agency.AgencyId, null), Ct));
        var detail = await AdministerAsync(host, s => s.UpdateSeasonsAsync(
            draftId,
            0,
            [new SeasonDefinition(SeasonLevel.High, new DateOnly(2027, 7, 1), new DateOnly(2027, 8, 31))],
            Ct));
        var season = detail.Seasons.Single();

        await AdministerAsync(host, s => s.UpdateSeasonsAsync(
            draftId,
            detail.Plan.Revision,
            [season with { EndDate = new DateOnly(2027, 9, 15), MinimumDays = 3 }],
            Ct));

        await using var scope = host.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CarRentalDbContext>();
        var versions = await db.Set<Season>().TemporalAll()
            .Where(s => s.Id == season.Id!.Value)
            .OrderBy(s => EF.Property<DateTime>(s, "ValidFrom"))
            .Select(s => new { s.EndDate, s.MinimumDays })
            .ToListAsync(Ct);
        versions.Select(v => (v.EndDate, v.MinimumDays)).ShouldBe([(new DateOnly(2027, 8, 31), 0), (new DateOnly(2027, 9, 15), 3)]);
        (await db.Set<RatePlan>().TemporalAll().CountAsync(p => p.Id == draftId, Ct)).ShouldBeGreaterThanOrEqualTo(3);

        var audit = await db.Set<AuditLog>()
            .Where(a => a.EntityType == "RatePlanSeason" && a.EntityId == season.Id.ToString())
            .OrderBy(a => a.Id)
            .ToListAsync(Ct);
        audit.Select(a => a.Action).ShouldBe([AuditActions.EntityCreated, AuditActions.EntityUpdated]);
        audit.ShouldAllBe(a => a.UserName == Manager.UserName);
        audit[1].After!.ShouldContain("2027-09-15");
    }

    [Fact]
    public async Task Publishing_numbers_the_versions_and_archives_the_replaced_plan()
    {
        var agency = await CreateAgencyAsync(host, Ct);
        var first = await PublishReferencePlanAsync(host, agency, Ct);
        first.Plan.Version.ShouldBe(1);

        var draftId = await AdministerAsync(host, s => s.CreateDraftAsync(new CreateDraftRequest(agency.AgencyId, null, "Grille v2"), Ct));
        var draft = await AdministerAsync(host, s => s.GetAsync(draftId, Ct));
        draft.Plan.BasedOnPlanId.ShouldBe(first.Plan.Id);
        draft.Prices.Count.ShouldBe(first.Prices.Count);
        var raised = draft.Prices.Select(p => p.Period == RatePeriodKey.ForSeason(SeasonLevel.High) ? p with { DailyPrice = Money.Tnd(150m) } : p).ToList();
        draft = await AdministerAsync(host, s => s.UpdatePricesAsync(draftId, draft.Plan.Revision, raised, Ct));
        var second = await AdministerAsync(host, s => s.PublishAsync(draftId, draft.Plan.Revision, Ct));

        second.Plan.Version.ShouldBe(2);
        var history = await AdministerAsync(host, s => s.GetHistoryAsync(agency.AgencyId, null, Ct));
        history.Select(p => (p.Id, p.Status, p.Version)).ShouldBe(
            [(second.Plan.Id, RatePlanStatus.Published, (int?)2), (first.Plan.Id, RatePlanStatus.Archived, 1)]);
        (await AdministerAsync(host, s => s.GetAsync(first.Plan.Id, Ct))).Plan.ArchivedAtUtc.ShouldNotBeNull();

        await using var scope = host.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CarRentalDbContext>();
        (await db.Set<CategoryPrice>().CountAsync(p => p.RatePlanId == first.Plan.Id, Ct)).ShouldBe(first.Prices.Count);
        (await db.Set<AuditLog>().AnyAsync(a => a.EntityType == nameof(RatePlan) && a.EntityId == first.Plan.Id.ToString() && a.After!.Contains("ArchivedAtUtc"), Ct))
            .ShouldBeTrue();
    }

    [Fact]
    public async Task Only_one_draft_exists_per_agency_and_channel()
    {
        var agency = await CreateAgencyAsync(host, Ct);
        await AdministerAsync(host, s => s.CreateDraftAsync(new CreateDraftRequest(agency.AgencyId, null), Ct));

        await using var scope = host.CreateScope(Manager);
        var service = scope.ServiceProvider.GetRequiredService<RatePlanAdministrationService>();

        (await service.CreateDraftAsync(new CreateDraftRequest(agency.AgencyId, null), Ct)).Error.ShouldBe(PricingErrors.DraftAlreadyExists);
        (await service.CreateDraftAsync(new CreateDraftRequest(agency.AgencyId, SalesChannel.BookingCom), Ct)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task A_stale_revision_is_refused()
    {
        var agency = await CreateAgencyAsync(host, Ct);
        var draftId = await AdministerAsync(host, s => s.CreateDraftAsync(new CreateDraftRequest(agency.AgencyId, null), Ct));
        await AdministerAsync(host, s => s.RenameAsync(draftId, 0, "Première modification", Ct));

        await using var scope = host.CreateScope(Manager);
        var service = scope.ServiceProvider.GetRequiredService<RatePlanAdministrationService>();

        (await service.RenameAsync(draftId, 0, "Modification concurrente", Ct)).Error.ShouldBe(PricingErrors.PlanModifiedConcurrently);
        (await service.GetAsync(draftId, Ct)).Value.Plan.Name.ShouldBe("Première modification");
    }
}
