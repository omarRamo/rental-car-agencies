using CarRental.Application.Common.Persistence;
using CarRental.Application.Common.Seeding;
using CarRental.Application.Fleet.Contracts;
using CarRental.Application.Referential.Contracts;
using CarRental.Domain.Common;
using CarRental.Domain.Pricing;
using Microsoft.Extensions.Logging;

namespace CarRental.Application.Pricing.Seeding;

/// <summary>
/// Demo rate plan v1 (published) of the demo agency: seasons from November 2026 to December 2028 (low November–March,
/// mid April–June and September–October, high July–August with 3 days minimum), Aïd and year-end events, the daily
/// prices of the five demo categories (ECMR matches the SPEC §7 example), tiers 0/5/10/15/20 %, the demo extras and
/// promotions, Booking.com at +15 %. Categories are found through the Fleet contract by ACRISS code (missing codes are
/// skipped with a warning). Runs once: nothing is done when the agency already has a plan.
/// </summary>
internal sealed partial class PricingDemoSeeder(
    IAppDbContext db,
    IRatePlanStore store,
    IFleetQueries fleet,
    IReferentialQueries referential,
    TimeProvider clock,
    ILogger<PricingDemoSeeder> logger) : IDataSeeder
{
    /// <summary>Name of the demo agency created by the Referential demo seeder.</summary>
    public const string DemoAgencyName = "Agence Démo Tunis";

    public const string DemoPlanName = "Grille démo 2026-2028";

    /// <summary>Daily prices TTC (low, mid, high season) of the demo categories.</summary>
    private static readonly (string Code, decimal Low, decimal Mid, decimal High)[] DemoPrices =
    [
        ("MCMR", 90m, 100m, 130m),
        ("ECMR", 100m, 110m, 140m),
        ("EDAR", 120m, 140m, 170m),
        ("CDMR", 130m, 150m, 190m),
        ("IFMR", 170m, 200m, 250m),
    ];

    public SeedStage Stage => SeedStage.Demo;

    public int Order => 500;

    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        var agency = (await referential.GetAgenciesAsync(cancellationToken)).FirstOrDefault(a => a.Name == DemoAgencyName);
        if (agency is null)
        {
            LogMissingAgency(DemoAgencyName);
            return;
        }

        if (await store.AnyForAgencyAsync(agency.Id, cancellationToken))
        {
            return;
        }

        var categories = new List<(Guid Id, decimal Low, decimal Mid, decimal High)>();
        foreach (var (code, low, mid, high) in DemoPrices)
        {
            if (await fleet.FindCategoryByCodeAsync(agency.Id, code, cancellationToken) is { } category)
            {
                categories.Add((category.Id, low, mid, high));
            }
            else
            {
                LogMissingCategory(code);
            }
        }

        if (categories.Count == 0)
        {
            LogNoPlan();
            return;
        }

        var locations = await referential.GetLocationsAsync(agency.Id, cancellationToken);
        var plan = Build(agency.Id, categories, locations, clock.GetUtcNow());
        store.Add(plan);
        await db.SaveChangesAsync(cancellationToken);
    }

    private static RatePlan Build(
        Guid agencyId,
        IReadOnlyList<(Guid Id, decimal Low, decimal Mid, decimal High)> categories,
        IReadOnlyList<LocationInfo> locations,
        DateTimeOffset now)
    {
        var plan = Expect(RatePlan.CreateDraft(agencyId, null, DemoPlanName, now));
        Expect(plan.ReplaceSeasons(Seasons(), now));
        Expect(plan.ReplaceEvents(Events(), now));

        var prices = new List<CategoryPriceDefinition>();
        foreach (var (categoryId, low, mid, high) in categories)
        {
            prices.Add(new CategoryPriceDefinition(categoryId, RatePeriodKey.Base, Money.Tnd(mid)));
            prices.Add(new CategoryPriceDefinition(categoryId, RatePeriodKey.ForSeason(SeasonLevel.Low), Money.Tnd(low)));
            prices.Add(new CategoryPriceDefinition(categoryId, RatePeriodKey.ForSeason(SeasonLevel.Mid), Money.Tnd(mid)));
            prices.Add(new CategoryPriceDefinition(categoryId, RatePeriodKey.ForSeason(SeasonLevel.High), Money.Tnd(high)));
            prices.AddRange(plan.Events.Select(e => new CategoryPriceDefinition(categoryId, RatePeriodKey.ForEvent(e.Id), Money.Tnd(high))));
        }

        Expect(plan.ReplacePrices(prices, now));
        Expect(plan.ReplaceTiers(
            [new(1, 0m), new(3, 5m), new(7, 10m), new(14, 15m), new(30, 20m)],
            [],
            now));
        Expect(plan.ReplaceExtras(Extras(locations), now));
        Expect(plan.ReplacePromotions(
            [
                new PromotionDefinition("Bienvenue 10 %", PromotionKind.Code, PromotionDiscountType.Percentage, DiscountPercent: 10m, Code: "BIENVENUE10"),
                new PromotionDefinition("Réservation anticipée", PromotionKind.EarlyBooking, PromotionDiscountType.Percentage, DiscountPercent: 5m, MinimumDays: 30),
                new PromotionDefinition("Long séjour", PromotionKind.LongStay, PromotionDiscountType.Percentage, DiscountPercent: 7m, MinimumDays: 14, IsCombinable: true),
            ],
            now));
        Expect(plan.ReplaceChannelRules([new ChannelRuleDefinition(SalesChannel.BookingCom, ChannelPricingMode.Markup, 15m)], now));
        Expect(plan.Publish(currentlyPublished: null, lastVersion: 0, now));
        return plan;
    }

    private static List<SeasonDefinition> Seasons()
    {
        var seasons = new List<SeasonDefinition> { new(SeasonLevel.Low, new DateOnly(2026, 11, 1), new DateOnly(2027, 3, 31)) };
        foreach (var year in new[] { 2027, 2028 })
        {
            seasons.Add(new SeasonDefinition(SeasonLevel.Mid, new DateOnly(year, 4, 1), new DateOnly(year, 6, 30)));
            seasons.Add(new SeasonDefinition(SeasonLevel.High, new DateOnly(year, 7, 1), new DateOnly(year, 8, 31), MinimumDays: 3));
            seasons.Add(new SeasonDefinition(SeasonLevel.Mid, new DateOnly(year, 9, 1), new DateOnly(year, 10, 31)));
            seasons.Add(new SeasonDefinition(
                SeasonLevel.Low,
                new DateOnly(year, 11, 1),
                year == 2027 ? new DateOnly(2028, 3, 31) : new DateOnly(2028, 12, 31)));
        }

        return seasons;
    }

    /// <summary>Demo dates (to be replaced by the agency): Aïd el-Fitr and Aïd el-Kébir, and the year-end holidays.</summary>
    private static PriorityEventDefinition[] Events() =>
    [
        new("Fin d'année 2026", new DateOnly(2026, 12, 24), new DateOnly(2027, 1, 2)),
        new("Aïd el-Fitr 2027", new DateOnly(2027, 3, 9), new DateOnly(2027, 3, 11)),
        new("Aïd el-Kébir 2027", new DateOnly(2027, 5, 16), new DateOnly(2027, 5, 18)),
        new("Fin d'année 2027", new DateOnly(2027, 12, 24), new DateOnly(2028, 1, 2)),
        new("Aïd el-Fitr 2028", new DateOnly(2028, 2, 26), new DateOnly(2028, 2, 28)),
        new("Aïd el-Kébir 2028", new DateOnly(2028, 5, 4), new DateOnly(2028, 5, 6)),
        new("Fin d'année 2028", new DateOnly(2028, 12, 24), new DateOnly(2029, 1, 2)),
    ];

    private static List<ExtraDefinition> Extras(IReadOnlyList<LocationInfo> locations)
    {
        var extras = new List<ExtraDefinition>
        {
            new("ADDITIONAL_DRIVER", "Conducteur additionnel", ExtraKind.Optional, ExtraPricingMode.PerDay, Money.Tnd(10m), NameEn: "Additional driver"),
            new("CHILD_SEAT", "Siège enfant", ExtraKind.Optional, ExtraPricingMode.PerDay, Money.Tnd(8m), Money.Tnd(50m), NameEn: "Child seat"),
            new("FRANCHISE_WAIVER", "Rachat de franchise", ExtraKind.FranchiseWaiver, ExtraPricingMode.PerDay, Money.Tnd(20m), NameEn: "Franchise waiver"),
            new("YOUNG_DRIVER", "Jeune conducteur", ExtraKind.YoungDriver, ExtraPricingMode.PerDay, Money.Tnd(15m), NameEn: "Young driver"),
            new("GPS", "GPS", ExtraKind.Optional, ExtraPricingMode.PerDay, Money.Tnd(5m)),
            new("HOTEL_DELIVERY", "Livraison à l'hôtel", ExtraKind.Optional, ExtraPricingMode.PerRental, Money.Tnd(30m), NameEn: "Hotel delivery"),
        };

        foreach (var (code, name, nameEn) in new[]
        {
            ("TUN", "Livraison aéroport Tunis-Carthage", "Tunis-Carthage airport delivery"),
            ("NBE", "Livraison aéroport Enfidha-Hammamet", "Enfidha-Hammamet airport delivery"),
        })
        {
            if (locations.FirstOrDefault(l => l.Code == code) is { } airport)
            {
                extras.Add(new ExtraDefinition($"DELIVERY_{code}", name, ExtraKind.LocationDelivery, ExtraPricingMode.PerRental, Money.Zero, LocationId: airport.Id, NameEn: nameEn));
            }
        }

        extras.Add(new ExtraDefinition("AFTER_HOURS", "Hors horaires", ExtraKind.AfterHours, ExtraPricingMode.PerRental, Money.Tnd(25m), NameEn: "After-hours"));
        extras.Add(new ExtraDefinition("ONE_WAY", "Aller simple", ExtraKind.OneWay, ExtraPricingMode.PerRental, Money.Tnd(60m), NameEn: "One-way"));
        return extras;
    }

    private static T Expect<T>(Result<T> result) =>
        result.IsSuccess ? result.Value : throw new InvalidOperationException($"Invalid demo rate plan: {result.Error}.");

    private static void Expect(Result result)
    {
        if (result.IsFailure)
        {
            throw new InvalidOperationException($"Invalid demo rate plan: {result.Error}.");
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Demo agency {AgencyName} not found: no demo rate plan.")]
    private partial void LogMissingAgency(string agencyName);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Demo category {AcrissCode} not found in the fleet: it is left out of the demo rate plan.")]
    private partial void LogMissingCategory(string acrissCode);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No demo category found in the fleet: no demo rate plan.")]
    private partial void LogNoPlan();
}
