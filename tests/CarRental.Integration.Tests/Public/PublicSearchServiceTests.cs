using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Fleet.Contracts;
using CarRental.Application.Pricing.Contracts;
using CarRental.Application.Referential.Contracts;
using CarRental.Domain.Common;
using CarRental.Web.Components.Public.Search;
using CarRental.Web.Components.Public.Site;
using Microsoft.Extensions.Logging.Abstractions;

namespace CarRental.Integration.Tests.Public;

/// <summary>Search orchestration over doubles of the availability and pricing contracts, without host.</summary>
public sealed class PublicSearchServiceTests
{
    private static readonly Guid AgencyId = Guid.CreateVersion7();
    private static readonly LocationInfo Airport = new(Guid.CreateVersion7(), AgencyId, "TUN", "Aéroport", LocationKindInfo.Airport, null);
    private static readonly DateTimeOffset Pickup = new(2027, 7, 1, 9, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static VehicleCategoryInfo Category(string code, int order, TransmissionInfo transmission = TransmissionInfo.Manual, int seats = 5) => new(
        Guid.CreateVersion7(), AgencyId, code, $"Catégorie {code}", "Modèle", seats, 5, 1, 2, transmission, FuelTypeInfo.Petrol, true,
        Money.Tnd(600m), Money.Tnd(300m), null, order, true);

    private static SearchCriteria Criteria(SearchQuery? query = null) =>
        new(Airport, Airport, Pickup, Pickup.AddDays(4), query ?? SearchQuery.Empty);

    [Fact]
    public void Gearbox_and_seat_filters_select_the_categories()
    {
        var auto = Category("EDAR", 3, TransmissionInfo.Automatic);
        var small = Category("MCMR", 1, seats: 4);

        PublicSearchService.Matches(auto, SearchQuery.Empty with { Gearbox = GearboxFilter.Automatic }).ShouldBeTrue();
        PublicSearchService.Matches(small, SearchQuery.Empty with { Gearbox = GearboxFilter.Automatic }).ShouldBeFalse();
        PublicSearchService.Matches(small, SearchQuery.Empty with { Gearbox = GearboxFilter.Manual }).ShouldBeTrue();
        PublicSearchService.Matches(small, SearchQuery.Empty with { MinimumSeats = 5 }).ShouldBeFalse();
        PublicSearchService.Matches(auto, SearchQuery.Empty with { MinimumSeats = 5 }).ShouldBeTrue();
    }

    [Fact]
    public async Task Available_categories_are_priced_and_sorted_by_total_with_the_requested_one_first()
    {
        var mini = Category("MCMR", 1);
        var eco = Category("ECMR", 2);
        var suv = Category("IFMR", 5);
        var world = new SearchWorld([new(mini, 3), new(eco, 1), new(suv, 2)], new() { [mini.Id] = 300m, [eco.Id] = 350m, [suv.Id] = 600m });

        var ascending = await world.Service.SearchAsync(Criteria(), Ct);
        var descending = await world.Service.SearchAsync(Criteria(SearchQuery.Empty with { Sort = ResultSort.PriceDescending }), Ct);
        var highlighted = await world.Service.SearchAsync(Criteria(SearchQuery.Empty with { Category = AcrissCode.Create("IFMR").Value }), Ct);

        ascending.Kind.ShouldBe(SearchOutcomeKind.Offers);
        ascending.Offers.Select(o => o.Category.AcrissCode).ShouldBe(["MCMR", "ECMR", "IFMR"]);
        ascending.Offers[1].Remaining.ShouldBe(1);
        ascending.Offers[0].Total.ShouldBe(Money.Tnd(300m));
        ascending.Offers[0].PerDay.ShouldBe(Money.Tnd(75m));
        descending.Offers.Select(o => o.Category.AcrissCode).ShouldBe(["IFMR", "ECMR", "MCMR"]);
        highlighted.Offers.Select(o => o.Category.AcrissCode).ShouldBe(["IFMR", "MCMR", "ECMR"]);
        world.Requests.ShouldAllBe(r => r.Channel == SalesChannel.Web && r.PickupLocationId == Airport.Id && r.Extras == null);
    }

    [Fact]
    public async Task A_whatsapp_link_is_searched_and_priced_on_the_whatsapp_channel()
    {
        var eco = Category("ECMR", 2);
        var world = new SearchWorld([new(eco, 2)], new() { [eco.Id] = 350m });
        var whatsapp = SearchQuery.Empty with { Traffic = new TrafficSource("whatsapp") };

        await world.Service.SearchAsync(Criteria(whatsapp), Ct);

        world.AvailabilityChannels.ShouldBe([SalesChannel.WhatsApp]);
        world.Requests.ShouldHaveSingleItem().Channel.ShouldBe(SalesChannel.WhatsApp);
    }

    [Fact]
    public async Task Empty_outcomes_say_why()
    {
        var eco = Category("ECMR", 2);
        var auto = SearchQuery.Empty with { Gearbox = GearboxFilter.Automatic };

        (await new SearchWorld([], new()).Service.SearchAsync(Criteria(), Ct)).Kind.ShouldBe(SearchOutcomeKind.NoAvailability);
        (await new SearchWorld([new(eco, 2)], new() { [eco.Id] = 350m }).Service.SearchAsync(Criteria(auto), Ct)).Kind.ShouldBe(SearchOutcomeKind.NoMatch);
        var unpriced = await new SearchWorld([new(eco, 2)], new()).Service.SearchAsync(Criteria(), Ct);
        unpriced.Kind.ShouldBe(SearchOutcomeKind.PricingUnavailable);
        unpriced.PricingFailures.ShouldBe(1);
    }

    [Fact]
    public async Task A_stay_shorter_than_the_season_minimum_reports_the_required_days_once()
    {
        var mini = Category("MCMR", 1);
        var eco = Category("ECMR", 2);
        var world = new SearchWorld([new(mini, 2), new(eco, 2)], new(), minimumDays: 3);

        var outcome = await world.Service.SearchAsync(Criteria(), Ct);

        outcome.Kind.ShouldBe(SearchOutcomeKind.MinimumDuration);
        outcome.RequiredMinimumDays.ShouldBe(3);
        world.Requests.Count.ShouldBe(1);
    }

    [Theory]
    [InlineData("2026-10-20", "2026-10-24")]
    [InlineData("2026-10-23", "2026-10-24")]
    [InlineData("2026-10-24", "2026-10-31")]
    [InlineData("2026-10-25", "2026-10-31")]
    public void The_home_prices_are_for_the_next_saturday(string today, string expected)
    {
        FeaturedOffers.NextSaturday(DateOnly.Parse(today, System.Globalization.CultureInfo.InvariantCulture))
            .ShouldBe(DateOnly.Parse(expected, System.Globalization.CultureInfo.InvariantCulture));
    }

    private sealed class SearchWorld(IReadOnlyList<AvailableCategoryInfo> available, Dictionary<Guid, decimal> totals, int? minimumDays = null)
        : IAvailabilityQueries, IPricingService
    {
        public List<PriceRequest> Requests { get; } = [];

        public List<SalesChannel> AvailabilityChannels { get; } = [];

        public PublicSearchService Service => new(this, this, NullLogger<PublicSearchService>.Instance);

        public Task<IReadOnlyList<AvailableCategoryInfo>> GetAvailableCategoriesAsync(Guid agencyId, Period rental, SalesChannel channel, CancellationToken cancellationToken = default)
        {
            AvailabilityChannels.Add(channel);
            return Task.FromResult(available);
        }

        public Task<CategoryAvailabilityInfo?> GetCategoryAvailabilityAsync(Guid categoryId, Period rental, SalesChannel channel, Guid? excludedBookingId = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<CapacitySegmentInfo>?> GetCapacityProfileAsync(Guid categoryId, Period range, SalesChannel? channel = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Result<PriceQuote>> QuoteAsync(PriceRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            if (minimumDays is { } days)
            {
                return Task.FromResult<Result<PriceQuote>>(new Error(PricingErrorCodes.MinimumDurationNotMet, $"The stay is too short (minimum: {days} days)."));
            }

            return Task.FromResult(totals.TryGetValue(request.CategoryId, out var total)
                ? Result.Success(Quote(request, Money.Tnd(total)))
                : (Result<PriceQuote>)new Error(PricingErrorCodes.CategoryNotPriced, "No price."));
        }

        private static PriceQuote Quote(PriceRequest request, Money total) => new(
            Guid.CreateVersion7(), 1, 4, [], total, Money.Zero, total, total, Money.Tnd(600m), false, [], PromoCodeOutcome.None, [],
            new PriceQuoteInputs(
                request.AgencyId, request.CategoryId, "XXXX", request.PickupLocationId, request.ReturnLocationId, request.PickupAt, request.ReturnAt,
                request.Channel, [], [], null, null, null, DateTimeOffset.UtcNow, ChannelPricingModeInfo.Parity, 0m, Guid.CreateVersion7(), 1, 0.19m,
                Money.Tnd(2m), TimeSpan.FromMinutes(59), true, 25, new TimeOnly(8, 0), new TimeOnly(20, 0), Money.Tnd(600m), Money.Tnd(300m)));
    }
}
