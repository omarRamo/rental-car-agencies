using CarRental.Application.Pricing;
using CarRental.Application.Pricing.Administration;
using CarRental.Application.Pricing.Contracts;
using CarRental.Application.Referential.Contracts;
using CarRental.Application.Tests.Fakes;
using CarRental.Domain.Common;
using CarRental.Domain.Pricing;
using Microsoft.Extensions.Time.Testing;

namespace CarRental.Application.Tests.Pricing;

/// <summary>An agency with two locations, the ECMR category and in-memory plans, plus factories for the pricing services.</summary>
internal sealed class PricingWorld
{
    public static readonly Guid AgencyId = Guid.Parse("0199b000-0000-7000-8000-000000000001");
    public static readonly Guid OtherAgencyId = Guid.Parse("0199b000-0000-7000-8000-000000000002");
    public static readonly Guid Office = Guid.Parse("0199b000-0000-7000-8000-0000000000a1");
    public static readonly Guid Airport = Guid.Parse("0199b000-0000-7000-8000-0000000000a2");
    public static readonly Guid Ecmr = Guid.Parse("0199b000-0000-7000-8000-0000000000e1");

    /// <param name="now">Start of the clock; by default 27/06/2027 09:00 Tunis, the day before the reference pick-up (no early booking).</param>
    public PricingWorld(DateTimeOffset? now = null)
    {
        Clock = new FakeTimeProvider(now ?? new DateTimeOffset(2027, 6, 27, 8, 0, 0, TimeSpan.Zero));
        Referential.Agencies.Add(new AgencyInfo(AgencyId, "Agence Test", "Tunis", null, null, IsActive: true));
        Referential.Agencies.Add(new AgencyInfo(OtherAgencyId, "Autre agence", "Sousse", null, null, IsActive: true));
        Referential.Locations.Add(new LocationInfo(Office, AgencyId, "AGC", "Agence", LocationKindInfo.AgencyOffice, null));
        Referential.Locations.Add(new LocationInfo(Airport, AgencyId, "TUN", "Aéroport", LocationKindInfo.Airport, null));
        Fleet.AddCategory(Ecmr, AgencyId, "ECMR", 600m, 300m);
    }

    public InMemoryRatePlanStore Store { get; } = new();

    public FakeFleetQueries Fleet { get; } = new();

    public FakeReferentialQueries Referential { get; } = new();

    public FakeSettingsReader Settings { get; } = new();

    public FakeTimeProvider Clock { get; }

    public InlineUnitOfWork UnitOfWork { get; } = new();

    public static DateTimeOffset Tunis(int year, int month, int day, int hour = 10, int minute = 0) =>
        TunisTime.FromLocal(new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Unspecified));

    /// <summary>The SPEC §7 request: ECMR, 28/06/2027 10:00 → 03/07/2027 10:00, one additional driver and one child seat.</summary>
    public static PriceRequest ReferenceRequest(SalesChannel channel = SalesChannel.Web) => new(
        AgencyId,
        Ecmr,
        Office,
        Office,
        Tunis(2027, 6, 28),
        Tunis(2027, 7, 3),
        channel,
        [new SelectedExtra("ADDITIONAL_DRIVER", 1), new SelectedExtra("CHILD_SEAT", 1)]);

    /// <summary>A draft with the reference grid (mid 110 until 30/06/2027, high 140), tiers, extras and promotions.</summary>
    public RatePlan Draft(SalesChannel? channel = null, decimal mid = 110m, decimal high = 140m, Guid? agencyId = null)
    {
        var now = Clock.GetUtcNow();
        var plan = RatePlan.CreateDraft(agencyId ?? AgencyId, channel, channel is null ? "Grille" : $"Grille {channel}", now).Value;
        Succeed(plan.ReplaceSeasons(
            [
                new SeasonDefinition(SeasonLevel.Mid, new DateOnly(2027, 4, 1), new DateOnly(2027, 6, 30)),
                new SeasonDefinition(SeasonLevel.High, new DateOnly(2027, 7, 1), new DateOnly(2027, 8, 31)),
            ],
            now));
        Succeed(plan.ReplacePrices(
            [
                new CategoryPriceDefinition(Ecmr, RatePeriodKey.Base, Money.Tnd(100m)),
                new CategoryPriceDefinition(Ecmr, RatePeriodKey.ForSeason(SeasonLevel.Mid), Money.Tnd(mid)),
                new CategoryPriceDefinition(Ecmr, RatePeriodKey.ForSeason(SeasonLevel.High), Money.Tnd(high)),
            ],
            now));
        Succeed(plan.ReplaceTiers([new(1, 0m), new(3, 5m), new(7, 10m), new(14, 15m), new(30, 20m)], [], now));
        Succeed(plan.ReplaceExtras(
            [
                new ExtraDefinition("ADDITIONAL_DRIVER", "Conducteur additionnel", ExtraKind.Optional, ExtraPricingMode.PerDay, Money.Tnd(10m)),
                new ExtraDefinition("CHILD_SEAT", "Siège enfant", ExtraKind.Optional, ExtraPricingMode.PerDay, Money.Tnd(8m), Money.Tnd(50m)),
                new ExtraDefinition("YOUNG_DRIVER", "Jeune conducteur", ExtraKind.YoungDriver, ExtraPricingMode.PerDay, Money.Tnd(15m)),
                new ExtraDefinition("OLD_GPS", "Ancien GPS", ExtraKind.Optional, ExtraPricingMode.PerDay, Money.Tnd(3m), IsActive: false),
            ],
            now));
        Succeed(plan.ReplacePromotions(
            [new PromotionDefinition("Anticipée", PromotionKind.EarlyBooking, PromotionDiscountType.Percentage, DiscountPercent: 5m, MinimumDays: 30)],
            now));
        Store.Add(plan);
        return plan;
    }

    /// <summary>Publishes a new default plan (Booking.com at +15 % unless other rules are given).</summary>
    public RatePlan PublishDefault(IReadOnlyList<ChannelRuleDefinition>? rules = null, decimal mid = 110m, decimal high = 140m)
    {
        var plan = Draft(mid: mid, high: high);
        Succeed(plan.ReplaceChannelRules(rules ?? [new ChannelRuleDefinition(SalesChannel.BookingCom, ChannelPricingMode.Markup, 15m)], Clock.GetUtcNow()));
        Publish(plan);
        return plan;
    }

    public RatePlan PublishChannelPlan(SalesChannel channel, decimal mid, decimal high)
    {
        var plan = Draft(channel, mid, high);
        Publish(plan);
        return plan;
    }

    public void Publish(RatePlan plan)
    {
        var current = Store.Plans.Find(p => p.AgencyId == plan.AgencyId && p.Channel == plan.Channel && p.Status == RatePlanStatus.Published);
        var last = Store.Plans.Where(p => p.AgencyId == plan.AgencyId && p.Channel == plan.Channel).Max(p => p.Version) ?? 0;
        Succeed(plan.Publish(current, last, Clock.GetUtcNow()));
    }

    public QuoteCalculator Calculator() => new(new RatePlanSelector(Store), Fleet, Referential, Settings);

    public IPricingService PricingService() => new PricingService(Calculator(), Clock);

    public IPricingQueries Queries() => new PricingQueries(Store, new RatePlanSelector(Store));

    public RatePlanAdministrationService Administration(string role) =>
        new(Store, Calculator(), Referential, Fleet, TestUsers.ContextFor(Guid.CreateVersion7(), $"{role}@demo.local", role), UnitOfWork, Clock);

    private static void Succeed(Result result) => result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error.ToString() : null);
}
