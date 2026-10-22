using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Fleet.Contracts;
using CarRental.Application.Pricing.Contracts;
using CarRental.Application.Referential.Contracts;
using CarRental.Domain.Common;

namespace CarRental.Application.Tests.Messaging.Agent.Fakes;

/// <summary>
/// The agency the agent works for in the tests and the evaluation: an office (AGC) and two airports (TUN, NBE), five
/// categories, availability and daily prices that a test or a scenario sets, the locked quotes created, and failures to
/// inject. Prices follow a small engine: days × daily price + the daily contribution (2.380 TTC per day).
/// </summary>
internal sealed class AgentWorld
{
    public static readonly Money DailyContribution = Money.Tnd(2.380m);

    public AgentWorld()
    {
        Agency = new AgencyInfo(Guid.CreateVersion7(), "Agence Démo Tunis", "Tunis", "+216 70 000 000", "contact@demo.local", true, "1 avenue Habib Bourguiba, 1000 Tunis");
        Office = new LocationInfo(Guid.CreateVersion7(), Agency.Id, "AGC", "Agence Tunis Centre", LocationKindInfo.AgencyOffice, "1 avenue Habib Bourguiba, 1000 Tunis");
        Carthage = new LocationInfo(Guid.CreateVersion7(), Agency.Id, "TUN", "Aéroport Tunis-Carthage", LocationKindInfo.Airport, "Aéroport international de Tunis-Carthage, 2035 Tunis");
        Enfidha = new LocationInfo(Guid.CreateVersion7(), Agency.Id, "NBE", "Aéroport Enfidha-Hammamet", LocationKindInfo.Airport, "Aéroport international d'Enfidha-Hammamet, 4030 Enfidha");
        Categories =
        [
            Category("MCMR", "Citadine", "Kia Picanto", 4, 1, 1, TransmissionInfo.Manual, 600m, 300m, 1),
            Category("ECMR", "Économique", "Peugeot 208", 5, 1, 2, TransmissionInfo.Manual, 800m, 400m, 2),
            Category("EDAR", "Économique automatique", "Renault Clio", 5, 1, 2, TransmissionInfo.Automatic, 900m, 450m, 3),
            Category("CDMR", "Compacte", "Volkswagen Golf", 5, 2, 2, TransmissionInfo.Manual, 1000m, 500m, 4),
            Category("IFMR", "SUV", "Hyundai Tucson", 5, 3, 2, TransmissionInfo.Manual, 1500m, 750m, 5),
        ];
        DailyPrices = new Dictionary<string, decimal>(StringComparer.Ordinal)
        {
            ["MCMR"] = 90m,
            ["ECMR"] = 110m,
            ["EDAR"] = 135m,
            ["CDMR"] = 150m,
            ["IFMR"] = 220m,
        };
    }

    public AgencyInfo Agency { get; }

    public LocationInfo Office { get; }

    public LocationInfo Carthage { get; }

    public LocationInfo Enfidha { get; }

    public IReadOnlyList<LocationInfo> Locations => [Office, Carthage, Enfidha];

    public IReadOnlyList<VehicleCategoryInfo> Categories { get; }

    public Dictionary<string, decimal> DailyPrices { get; }

    /// <summary>Codes of the categories available (null: all); a period predicate can refine it.</summary>
    public HashSet<string>? AvailableCodes { get; set; }

    /// <summary>Whether a period has any availability at all (true by default).</summary>
    public Func<Period, bool> PeriodAvailable { get; set; } = _ => true;

    /// <summary>The pricing engine fails (tool failure).</summary>
    public bool PricingThrows { get; set; }

    public int? MinimumDays { get; set; }

    public List<PriceRequest> PriceRequests { get; } = [];

    public List<LockedQuoteInfo> LockedQuotes { get; } = [];

    public VehicleCategoryInfo CategoryByCode(string code) => Categories.Single(c => c.AcrissCode == code);

    public static int DaysOf(DateTimeOffset pickupAt, DateTimeOffset returnAt) => Math.Max(1, (int)Math.Ceiling((returnAt - pickupAt - TimeSpan.FromMinutes(59)).TotalDays));

    public Money TotalOf(string code, int days) => Money.Tnd(DailyPrices[code] * days) + DailyContribution * days;

    public PriceQuote Quote(PriceRequest request)
    {
        var category = Categories.Single(c => c.Id == request.CategoryId);
        var days = DaysOf(request.PickupAt, request.ReturnAt);
        var daily = Money.Tnd(DailyPrices[category.AcrissCode]);
        var rental = daily * days;
        var contribution = DailyContribution * days;
        var total = rental + contribution;
        var lines = new List<PriceQuoteLine>
        {
            new(QuoteLineKind.Rental, "RENTAL", "Quote.Line.Rental", [], days, daily, Money.Tnd(rental.Amount / 1.19m), rental - Money.Tnd(rental.Amount / 1.19m), rental, 0.19m),
            new(QuoteLineKind.DailyContribution, "DAILY_CONTRIBUTION", "Quote.Line.DailyContribution", [], days, DailyContribution, Money.Tnd(2m * days), contribution - Money.Tnd(2m * days), contribution, 0.19m),
        };
        var inputs = new PriceQuoteInputs(
            request.AgencyId, category.Id, category.AcrissCode, request.PickupLocationId, request.ReturnLocationId, request.PickupAt, request.ReturnAt,
            request.Channel, [], [], null, null, null, DateTimeOffset.UnixEpoch, ChannelPricingModeInfo.Parity, 0m, Guid.Empty, 1, 0.19m, Money.Tnd(2m),
            TimeSpan.FromMinutes(59), true, 25, new TimeOnly(8, 0), new TimeOnly(20, 0), category.Deposit, category.ReducedDeposit);
        return new PriceQuote(
            Guid.Empty, 1, days, lines, Money.Tnd(total.Amount / 1.19m), total - Money.Tnd(total.Amount / 1.19m), total, rental, category.Deposit, false, [],
            PromoCodeOutcome.None, [], inputs);
    }

    private VehicleCategoryInfo Category(
        string code, string name, string model, int seats, int largeBags, int smallBags, TransmissionInfo gearbox, decimal deposit, decimal reduced, int order) =>
        new(Guid.CreateVersion7(), Agency.Id, code, name, model, seats, 5, largeBags, smallBags, gearbox, FuelTypeInfo.Petrol, true,
            Money.Tnd(deposit), Money.Tnd(reduced), null, order, true);
}

internal sealed class FakeAgentReferential(AgentWorld world) : IReferentialQueries
{
    public Task<IReadOnlyList<AgencyInfo>> GetAgenciesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<AgencyInfo>>([world.Agency]);

    public Task<IReadOnlyList<LocationInfo>> GetLocationsAsync(Guid? agencyId = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(world.Locations);

    public Task<LocationInfo?> FindLocationByCodeAsync(Guid agencyId, string code, CancellationToken cancellationToken = default) =>
        Task.FromResult(world.Locations.FirstOrDefault(l => string.Equals(l.Code, code, StringComparison.OrdinalIgnoreCase)));
}

internal sealed class FakeAgentFleet(AgentWorld world) : IFleetQueries
{
    public Task<IReadOnlyList<VehicleCategoryInfo>> GetCategoriesAsync(Guid? agencyId = null, bool includeInactive = false, CancellationToken cancellationToken = default) =>
        Task.FromResult(world.Categories);

    public Task<VehicleCategoryInfo?> GetCategoryAsync(Guid categoryId, CancellationToken cancellationToken = default) =>
        Task.FromResult(world.Categories.FirstOrDefault(c => c.Id == categoryId));

    public Task<VehicleCategoryInfo?> FindCategoryByCodeAsync(Guid agencyId, string acrissCode, CancellationToken cancellationToken = default) =>
        Task.FromResult(world.Categories.FirstOrDefault(c => c.AcrissCode == acrissCode));

    public Task<VehicleInfo?> GetVehicleAsync(Guid vehicleId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<IReadOnlyList<VehicleInfo>> GetVehiclesAsync(Guid categoryId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<IReadOnlyList<VehicleAvailabilityInfo>> GetAvailabilityAsync(Guid categoryId, Period range, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<VehicleDeadlineInfo>> GetDeadlinesAsync(Guid vehicleId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}

internal sealed class FakeAgentAvailability(AgentWorld world) : IAvailabilityQueries
{
    public List<(Period Period, SalesChannel Channel)> Requests { get; } = [];

    public Task<IReadOnlyList<AvailableCategoryInfo>> GetAvailableCategoriesAsync(Guid agencyId, Period rental, SalesChannel channel, CancellationToken cancellationToken = default)
    {
        Requests.Add((rental, channel));
        IReadOnlyList<AvailableCategoryInfo> available = world.PeriodAvailable(rental)
            ? [.. world.Categories.Where(c => world.AvailableCodes is null || world.AvailableCodes.Contains(c.AcrissCode)).Select(c => new AvailableCategoryInfo(c, 2))]
            : [];
        return Task.FromResult(available);
    }

    public Task<CategoryAvailabilityInfo?> GetCategoryAvailabilityAsync(Guid categoryId, Period rental, SalesChannel channel, Guid? excludedBookingId = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<CapacitySegmentInfo>?> GetCapacityProfileAsync(Guid categoryId, Period range, SalesChannel? channel = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
}

internal sealed class FakeAgentPricing(AgentWorld world) : IPricingService
{
    public Task<Result<PriceQuote>> QuoteAsync(PriceRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        world.PriceRequests.Add(request);
        if (world.PricingThrows)
        {
            throw new InvalidOperationException("Simulated failure of the pricing engine.");
        }

        if (world.MinimumDays is { } minimum && AgentWorld.DaysOf(request.PickupAt, request.ReturnAt) < minimum)
        {
            return Task.FromResult(Result.Failure<PriceQuote>(Error.Validation(PricingErrorCodes.MinimumDurationNotMet, $"Too short (minimum: {minimum} days).")));
        }

        return Task.FromResult(Result.Success(world.Quote(request)));
    }
}

internal sealed class FakeAgentBookingRequests(AgentWorld world) : IBookingRequests
{
    private int _sequence;

    public Task<Result<LockedQuoteInfo>> CreateLockedQuoteAsync(PriceRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var quote = world.Quote(request);
        var code = $"Q{Interlocked.Increment(ref _sequence):D2}F3K";
        var locked = new LockedQuoteInfo(
            Guid.CreateVersion7(), code, request.AgencyId, request.CategoryId, request.Channel, request.PickupLocationId, request.ReturnLocationId,
            request.PickupAt, request.ReturnAt, quote.Days, quote.TotalInclVat, quote.Deposit, [], null, null, DateTimeOffset.UnixEpoch.AddYears(60), false);
        world.LockedQuotes.Add(locked);
        return Task.FromResult(Result.Success(locked));
    }

    public Task<LockedQuoteInfo?> GetLockedQuoteAsync(string code, CancellationToken cancellationToken = default) =>
        Task.FromResult(world.LockedQuotes.FirstOrDefault(q => q.Code == code));

    public Task<Result<BookingRequestResult>> CreateDraftAsync(BookingRequestDraft draft, CancellationToken cancellationToken = default) => throw new NotSupportedException("The bot never creates a booking.");

    public Task<Result<BookingRequestResult>> SubmitAsync(Guid bookingId, BookingRequestParties parties, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The bot never creates a booking.");

    public Task<Result<BookingRequestResult>> SubmitRequestAsync(BookingRequestDraft draft, BookingRequestParties parties, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The bot never creates a booking.");

    public Task<Result> ProvideInformationAsync(Guid bookingId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}
