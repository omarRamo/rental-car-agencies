using CarRental.Application.Common.Seeding;
using CarRental.Application.Fleet.Contracts;
using CarRental.Application.Pricing.Administration;
using CarRental.Application.Pricing.Contracts;
using CarRental.Application.Referential.Contracts;
using CarRental.Domain.Common;
using CarRental.Domain.Pricing;
using CarRental.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static CarRental.Integration.Tests.Pricing.PricingScenario;

namespace CarRental.Integration.Tests.Pricing;

/// <summary><see cref="IPricingService"/>, <see cref="IPricingQueries"/> and the demo plan on SQL Server, with the Fleet stub.</summary>
public sealed class PricingServiceIntegrationTests(IntegrationHost host)
{
    private const string DemoAgencyName = "Agence Démo Tunis";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_reference_example_is_priced_from_the_published_plan_in_the_database()
    {
        var agency = await CreateAgencyAsync(host, Ct);
        var plan = await PublishReferencePlanAsync(host, agency, Ct);

        await using var scope = host.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<IPricingService>().QuoteAsync(ReferenceRequest(agency), Ct);

        result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error.ToString() : null);
        var quote = result.Value;
        quote.TotalInclVat.ShouldBe(Money.Tnd(681.400m));
        quote.TotalExclVat.ShouldBe(Money.Tnd(572.605m));
        quote.TotalVat.ShouldBe(Money.Tnd(108.795m));
        quote.Lines.Select(l => l.InclVat.Amount).ShouldBe([330m, 280m, -30.5m, 50m, 40m, 11.9m]);
        quote.RatePlanId.ShouldBe(plan.Plan.Id);
        quote.RatePlanVersion.ShouldBe(1);
        quote.Deposit.ShouldBe(Money.Tnd(600m));
        quote.Inputs.CategoryCode.ShouldBe("ECMR");
    }

    [Fact]
    public async Task Fleet_and_referential_data_feed_the_quote()
    {
        var agency = await CreateAgencyAsync(host, Ct);
        await PublishReferencePlanAsync(host, agency, Ct);

        await using var scope = host.CreateScope();
        var pricing = scope.ServiceProvider.GetRequiredService<IPricingService>();
        var request = ReferenceRequest(agency) with
        {
            PickupLocationId = agency.AirportId,
            Extras = [new SelectedExtra("FRANCHISE_WAIVER", 1)],
            GuaranteedVehicleId = agency.GuaranteedEcmrVehicle,
        };

        var quote = (await pricing.QuoteAsync(request, Ct)).Value;

        quote.Deposit.ShouldBe(Money.Tnd(300m));
        quote.Lines.Single(l => l.Kind == QuoteLineKind.GuaranteedModel).InclVat.ShouldBe(Money.Tnd(75m));
        quote.Lines.Single(l => l.Kind == QuoteLineKind.Delivery).InclVat.ShouldBe(Money.Tnd(15m));
        quote.Lines.Single(l => l.Kind == QuoteLineKind.FranchiseWaiver).InclVat.ShouldBe(Money.Tnd(100m));
        (await pricing.QuoteAsync(request with { GuaranteedVehicleId = agency.PlainEcmrVehicle }, Ct)).Error
            .ShouldBe(PricingErrors.GuaranteedModelNotOffered);
        (await pricing.QuoteAsync(request with { PickupLocationId = Guid.CreateVersion7() }, Ct)).Error.ShouldBe(PricingErrors.LocationNotFound);
        (await pricing.QuoteAsync(request with { CategoryId = agency.Ifmr }, Ct)).Error.ShouldBe(PricingErrors.GuaranteedVehicleCategoryMismatch);
        (await pricing.QuoteAsync(request with { CategoryId = agency.Ifmr, GuaranteedVehicleId = null }, Ct)).Error
            .ShouldBe(PricingErrors.CategoryNotPriced);
        var tooShort = await pricing.QuoteAsync(request with { PickupAt = Tunis(2027, 7, 10), ReturnAt = Tunis(2027, 7, 12) }, Ct);
        PricingErrorCodes.TryGetRequiredMinimumDays(tooShort.Error, out var minimum).ShouldBeTrue();
        minimum.ShouldBe(3);
    }

    [Fact]
    public async Task Settings_stored_for_the_agency_override_the_platform_values()
    {
        var agency = await CreateAgencyAsync(host, Ct);
        await PublishReferencePlanAsync(host, agency, Ct);
        await using (var scope = host.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CarRentalDbContext>();
            db.Add(CarRental.Domain.Referential.Setting.Create(PricingSettings.AgencyOpeningHours.Key, "11:00-18:00", agency.AgencyId).Value);
            db.Add(CarRental.Domain.Referential.Setting.Create(WellKnownSettings.DailyContributionExclVat.Key, "3.000", agency.AgencyId).Value);
            await db.SaveChangesAsync(Ct);
        }

        await using var quoteScope = host.CreateScope();
        var quote = (await quoteScope.ServiceProvider.GetRequiredService<IPricingService>().QuoteAsync(ReferenceRequest(agency), Ct)).Value;

        quote.Inputs.OpensAt.ShouldBe(new TimeOnly(11, 0));
        quote.Lines.Single(l => l.Kind == QuoteLineKind.DailyContribution).ExclVat.ShouldBe(Money.Tnd(15m));
    }

    [Fact]
    public async Task Booking_com_is_priced_with_its_own_grid_when_its_rule_says_so()
    {
        var agency = await CreateAgencyAsync(host, Ct);
        var defaultPlan = await PublishReferencePlanAsync(host, agency, Ct);
        var draftId = await AdministerAsync(host, s => s.CreateDraftAsync(new CreateDraftRequest(agency.AgencyId, null), Ct));
        var draft = await AdministerAsync(host, s => s.UpdateChannelRulesAsync(
            draftId,
            0,
            [new ChannelRuleDefinition(SalesChannel.BookingCom, ChannelPricingMode.OwnGrid)],
            Ct));
        draft.Issues.ShouldContain(i => i.Code == PlanValidationCodes.ChannelPlanMissing);
        await AdministerAsync(host, s => s.PublishAsync(draftId, draft.Plan.Revision, Ct));

        await using (var scope = host.CreateScope())
        {
            (await scope.ServiceProvider.GetRequiredService<IPricingService>().QuoteAsync(ReferenceRequest(agency, SalesChannel.BookingCom), Ct)).Error
                .ShouldBe(PricingErrors.ChannelPlanMissing);
        }

        var channelDraftId = await AdministerAsync(host, s => s.CreateDraftAsync(new CreateDraftRequest(agency.AgencyId, SalesChannel.BookingCom, "Booking.com"), Ct));
        var channelDraft = await AdministerAsync(host, s => s.GetAsync(channelDraftId, Ct));
        channelDraft.Prices.Count.ShouldBe(defaultPlan.Prices.Count);
        var channelPrices = channelDraft.Prices.Select(p => p with { DailyPrice = p.DailyPrice + Money.Tnd(20m) }).ToList();
        channelDraft = await AdministerAsync(host, s => s.UpdatePricesAsync(channelDraftId, channelDraft.Plan.Revision, channelPrices, Ct));
        var channelPlan = await AdministerAsync(host, s => s.PublishAsync(channelDraftId, channelDraft.Plan.Revision, Ct));

        await using var quoteScope = host.CreateScope();
        var quote = (await quoteScope.ServiceProvider.GetRequiredService<IPricingService>().QuoteAsync(ReferenceRequest(agency, SalesChannel.BookingCom), Ct)).Value;
        quote.RatePlanId.ShouldBe(channelPlan.Plan.Id);
        quote.DailyRates.Select(r => r.Price.Amount).ShouldBe([130m, 130m, 130m, 160m, 160m]);
        quote.Inputs.ChannelPricingMode.ShouldBe(ChannelPricingModeInfo.OwnGrid);
    }

    [Fact]
    public async Task The_demo_agency_has_a_published_plan_v1_seeded_once()
    {
        await using var scope = host.CreateScope();
        var agency = (await scope.ServiceProvider.GetRequiredService<IReferentialQueries>().GetAgenciesAsync(Ct)).Single(a => a.Name == DemoAgencyName);
        var queries = scope.ServiceProvider.GetRequiredService<IPricingQueries>();

        var demoEcmr = (await scope.ServiceProvider.GetRequiredService<IFleetQueries>().FindCategoryByCodeAsync(agency.Id, "ECMR", Ct))!.Id;
        var summary = await queries.GetPublishedPlanAsync(agency.Id, cancellationToken: Ct);

        summary.ShouldNotBeNull();
        summary.Version.ShouldBe(1);
        summary.CategoryRates.Count.ShouldBe(5);
        summary.CategoryRates.Single(r => r.CategoryId == demoEcmr).ShouldSatisfyAllConditions(
            r => r.LowSeasonPrice.ShouldBe(Money.Tnd(100m)),
            r => r.MidSeasonPrice.ShouldBe(Money.Tnd(110m)),
            r => r.HighSeasonPrice.ShouldBe(Money.Tnd(140m)));
        summary.Seasons[0].StartDate.ShouldBe(new DateOnly(2026, 11, 1));
        summary.Seasons[^1].EndDate.ShouldBe(new DateOnly(2028, 12, 31));
        summary.Seasons.Where(s => s.Level == SeasonLevelInfo.High).ShouldAllBe(s => s.MinimumDays == 3);
        summary.Events.ShouldContain(e => e.Name.StartsWith("Fin d'année", StringComparison.Ordinal) && e.StartDate.Day == 24 && e.EndDate.Day == 2);
        summary.ChannelRules.ShouldBe([new ChannelRuleInfo(SalesChannel.BookingCom, ChannelPricingModeInfo.Markup, 15m)]);
        var extras = await queries.GetExtrasAsync(agency.Id, SalesChannel.Web, Ct);
        extras.Select(e => e.Code).ShouldBe(
            ["ADDITIONAL_DRIVER", "CHILD_SEAT", "FRANCHISE_WAIVER", "YOUNG_DRIVER", "GPS", "HOTEL_DELIVERY", "DELIVERY_TUN", "DELIVERY_NBE", "AFTER_HOURS", "ONE_WAY"]);

        // Running the seed step again changes nothing.
        var seeder = scope.ServiceProvider.GetServices<IDataSeeder>().Single(s => s.Stage == SeedStage.Demo && s.Order == 500);
        await seeder.SeedAsync(Ct);
        var db = scope.ServiceProvider.GetRequiredService<CarRentalDbContext>();
        (await db.Set<RatePlan>().CountAsync(p => p.AgencyId == agency.Id, Ct)).ShouldBe(1);
    }

    [Fact]
    public async Task The_demo_plan_reproduces_the_reference_example_and_marks_up_booking_com()
    {
        await using var scope = host.CreateScope(Manager);
        var referential = scope.ServiceProvider.GetRequiredService<IReferentialQueries>();
        var agency = (await referential.GetAgenciesAsync(Ct)).Single(a => a.Name == DemoAgencyName);
        var office = (await referential.FindLocationByCodeAsync(agency.Id, "AGC", Ct))!;
        var demoEcmr = (await scope.ServiceProvider.GetRequiredService<IFleetQueries>().FindCategoryByCodeAsync(agency.Id, "ECMR", Ct))!.Id;
        var request = new PriceRequest(
            agency.Id,
            demoEcmr,
            office.Id,
            office.Id,
            Tunis(2027, 6, 28),
            Tunis(2027, 7, 3),
            SalesChannel.Web,
            [new SelectedExtra("ADDITIONAL_DRIVER", 1), new SelectedExtra("CHILD_SEAT", 1)]);
        var simulator = scope.ServiceProvider.GetRequiredService<RatePlanAdministrationService>();

        // Booked the day before: no early booking promotion, as in SPEC §7.
        var web = (await simulator.SimulateAsync(new SimulationRequest(request, QuotedAt: Tunis(2027, 6, 27)), Ct)).Value.Published;
        var bookingCom = (await simulator.SimulateAsync(new SimulationRequest(request with { Channel = SalesChannel.BookingCom }, QuotedAt: Tunis(2027, 6, 27)), Ct)).Value.Published;
        var early = (await simulator.SimulateAsync(new SimulationRequest(request, QuotedAt: Tunis(2027, 4, 1)), Ct)).Value.Published;

        web.Quote!.TotalInclVat.ShouldBe(Money.Tnd(681.400m));
        web.Quote.TotalExclVat.ShouldBe(Money.Tnd(572.605m));
        bookingCom.Quote!.DailyRates.Select(r => r.Price.Amount).ShouldBe([127m, 127m, 127m, 161m, 161m]);
        early.Quote!.AppliedPromotions.ShouldHaveSingleItem().Kind.ShouldBe(PromotionKindInfo.EarlyBooking);
    }

    [Fact]
    public async Task Administration_checks_the_pricing_permissions()
    {
        var agency = await CreateAgencyAsync(host, Ct);
        var plan = await PublishReferencePlanAsync(host, agency, Ct);

        await using (var agentScope = host.CreateScope(Agent))
        {
            var service = agentScope.ServiceProvider.GetRequiredService<RatePlanAdministrationService>();
            (await service.ListAsync(agency.AgencyId, false, Ct)).Error.ShouldBe(PricingErrors.Forbidden);
            (await service.SimulateAsync(new SimulationRequest(ReferenceRequest(agency)), Ct)).Error.ShouldBe(PricingErrors.Forbidden);
        }

        await using var accountantScope = host.CreateScope(Accountant);
        var readOnly = accountantScope.ServiceProvider.GetRequiredService<RatePlanAdministrationService>();
        (await readOnly.ListAsync(agency.AgencyId, false, Ct)).Value.ShouldHaveSingleItem().Id.ShouldBe(plan.Plan.Id);
        (await readOnly.CreateDraftAsync(new CreateDraftRequest(agency.AgencyId, null), Ct)).Error.ShouldBe(PricingErrors.Forbidden);
        (await readOnly.ArchiveAsync(plan.Plan.Id, Ct)).Error.ShouldBe(PricingErrors.Forbidden);
    }
}
