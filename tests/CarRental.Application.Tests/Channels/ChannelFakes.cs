using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Channels.Restrictions;
using CarRental.Application.Common.Availability;
using CarRental.Application.Common.Persistence;
using CarRental.Application.Pricing.Contracts;
using CarRental.Application.Tests.Customers.Fakes;
using CarRental.Application.Tests.Fakes;
using CarRental.Domain.Channels;
using CarRental.Domain.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace CarRental.Application.Tests.Channels;

/// <summary>A channel of an agency, stored in an in-memory context with its restrictions, and the readers of the module.</summary>
internal sealed class ChannelWorld
{
    public ChannelWorld(Guid agencyId, FakeTimeProvider clock)
    {
        AgencyId = agencyId;
        Clock = clock;
        var stamp = Stamp;
        Channel = Channel.Create(agencyId, "booking-com", "Booking.com", SalesChannel.BookingCom, ChannelAdapterKind.Fake, ChannelPaymentMode.PayOnSite, 0.15m, stamp).Value;
        Channel.ChangeState(ChannelState.Manual, stamp).IsSuccess.ShouldBeTrue();
        Db.Set<Channel>().Add(Channel);
        var services = new ServiceCollection();
        services.AddSingleton<IAppDbContext>(Db);
        Provider = services.BuildServiceProvider();
        Cache = new ChannelRestrictionsCache(Provider.GetRequiredService<IServiceScopeFactory>(), clock, Notifier);
    }

    public Guid AgencyId { get; }

    public FakeTimeProvider Clock { get; }

    public InMemoryAppDbContext Db { get; } = new();

    public InlineUnitOfWork UnitOfWork { get; } = new();

    public CountingNotifier Notifier { get; } = new();

    public Channel Channel { get; }

    public ServiceProvider Provider { get; }

    public ChannelRestrictionsCache Cache { get; }

    public ChannelStamp Stamp => new(Clock.GetUtcNow(), "gerant@demo.local");

    public ChannelRestrictionReader Reader() => new(Db, UnitOfWork, Cache, Clock);

    public ChannelAvailabilityPolicy Policy() => new(Reader());

    public ChannelQuota StopSell(Guid categoryId, DateOnly from, DateOnly to, Guid? locationId = null)
    {
        var quota = ChannelQuota.StopSell(Channel, categoryId, locationId, from, to, null, Stamp).Value;
        Db.Set<ChannelQuota>().Add(quota);
        Cache.OnCommitted(new HashSet<Type> { typeof(ChannelQuota) });
        return quota;
    }

    public ChannelQuota Reserve(Guid categoryId, DateOnly from, DateOnly to, int vehicles)
    {
        var quota = ChannelQuota.Reserve(Channel, categoryId, from, to, vehicles, null, Stamp).Value;
        Db.Set<ChannelQuota>().Add(quota);
        Cache.OnCommitted(new HashSet<Type> { typeof(ChannelQuota) });
        return quota;
    }
}

/// <summary>Counts the availability change notifications.</summary>
internal sealed class CountingNotifier : IAvailabilityChangeNotifier
{
    public int All { get; private set; }

    public List<Guid> Categories { get; } = [];

    public void CategoryChanged(Guid categoryId) => Categories.Add(categoryId);

    public void AllChanged() => All++;
}

/// <summary>Capacity profiles per category for the outbound payloads.</summary>
internal sealed class ProfileAvailability : IAvailabilityQueries
{
    public Dictionary<Guid, IReadOnlyList<CapacitySegmentInfo>> Profiles { get; } = [];

    public List<AvailableCategoryInfo> Available { get; } = [];

    public Task<IReadOnlyList<AvailableCategoryInfo>> GetAvailableCategoriesAsync(Guid agencyId, Period rental, SalesChannel channel, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<AvailableCategoryInfo>>(Available);

    public Task<CategoryAvailabilityInfo?> GetCategoryAvailabilityAsync(Guid categoryId, Period rental, SalesChannel channel, Guid? excludedBookingId = null, CancellationToken cancellationToken = default) =>
        Task.FromResult<CategoryAvailabilityInfo?>(null);

    public Task<IReadOnlyList<CapacitySegmentInfo>?> GetCapacityProfileAsync(Guid categoryId, Period range, SalesChannel? channel = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(Profiles.TryGetValue(categoryId, out var profile) ? profile : (IReadOnlyList<CapacitySegmentInfo>?)[new CapacitySegmentInfo(range, 2, 0)]);
}

/// <summary>Quotes whose daily rates are a fixed price per category (no other line), and the published plan.</summary>
internal sealed class DailyRatePricing : IPricingService, IPricingQueries
{
    public Dictionary<Guid, decimal> DailyPrices { get; } = [];

    public RatePlanSummary? Plan { get; set; }

    public Task<Result<PriceQuote>> QuoteAsync(PriceRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!DailyPrices.TryGetValue(request.CategoryId, out var price))
        {
            return Task.FromResult(Result.Failure<PriceQuote>(Error.Validation("pricing.category_not_priced", "Not priced.")));
        }

        var days = Math.Max(1, (int)Math.Ceiling((request.ReturnAt - request.PickupAt).TotalDays));
        var first = TunisTime.DateOf(request.PickupAt);
        var daily = Enumerable.Range(0, days).Select(i => new DailyRateInfo(first.AddDays(i), RatePeriodKindInfo.Base, null, Money.Tnd(price), Money.Tnd(price))).ToList();
        var total = Money.Tnd(price * days);
        var inputs = new PriceQuoteInputs(
            request.AgencyId, request.CategoryId, "ECMR", request.PickupLocationId, request.ReturnLocationId, request.PickupAt, request.ReturnAt, request.Channel,
            [], [], null, null, null, request.PickupAt, ChannelPricingModeInfo.Parity, 0m, Guid.Empty, 1, 0.19m, Money.Tnd(2m), TimeSpan.FromMinutes(59), false, 25,
            new TimeOnly(8, 0), new TimeOnly(18, 0), Money.Tnd(600m), Money.Tnd(300m));
        return Task.FromResult(Result.Success(new PriceQuote(
            Guid.Empty, 1, days, [], total, Money.Zero, total, total, Money.Tnd(600m), false, [], PromoCodeOutcome.None, daily, inputs)));
    }

    public Task<IReadOnlyList<ExtraOffer>> GetExtrasAsync(Guid agencyId, SalesChannel channel, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ExtraOffer>>([]);

    public Task<RatePlanSummary?> GetPublishedPlanAsync(Guid agencyId, SalesChannel? channel = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(channel is null ? Plan : null);

    public static RatePlanSummary PlanVersion(Guid agencyId, int version) =>
        new(Guid.Parse("00000000-0000-0000-0000-00000000f00d"), agencyId, null, "Grille", version, DateTimeOffset.UnixEpoch, [], [], [], []);
}
