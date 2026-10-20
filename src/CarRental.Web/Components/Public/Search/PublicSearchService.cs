using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Fleet.Contracts;
using CarRental.Application.Pricing.Contracts;
using CarRental.Domain.Common;

namespace CarRental.Web.Components.Public.Search;

/// <summary>
/// Runs a public search: categories still available for the period on the channel (cached availability engine),
/// filtered by gearbox and seats, then priced one by one by the pricing engine (total TTC, extras none). Used by the
/// results page and <c>GET /api/public/availability</c>; nothing is taken from the URL but the criteria.
/// </summary>
public sealed partial class PublicSearchService(
    IAvailabilityQueries availability,
    IPricingService pricing,
    ILogger<PublicSearchService> logger)
{
    public async Task<SearchOutcome> SearchAsync(SearchCriteria criteria, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(criteria);
        var available = await availability.GetAvailableCategoriesAsync(criteria.AgencyId, criteria.Period, criteria.Channel, cancellationToken);
        var matching = available.Where(candidate => Matches(candidate.Category, criteria.Query)).ToList();

        var offers = new List<SearchOffer>(matching.Count);
        int? requiredMinimumDays = null;
        var pricingFailures = 0;
        foreach (var candidate in matching)
        {
            var quote = await pricing.QuoteAsync(
                new PriceRequest(
                    criteria.AgencyId,
                    candidate.Category.Id,
                    criteria.PickupLocation.Id,
                    criteria.ReturnLocation.Id,
                    criteria.PickupAt,
                    criteria.ReturnAt,
                    criteria.Channel),
                cancellationToken);
            if (quote.IsSuccess)
            {
                offers.Add(new SearchOffer(candidate.Category, candidate.Remaining, quote.Value));
            }
            else if (PricingErrorCodes.TryGetRequiredMinimumDays(quote.Error, out var minimumDays))
            {
                // The minimum duration comes from the seasons and events touched by the stay, whatever the category.
                requiredMinimumDays = minimumDays;
                break;
            }
            else
            {
                pricingFailures++;
                LogNotPriced(candidate.Category.AcrissCode, quote.Error.Code);
            }
        }

        return new SearchOutcome(Order(offers, criteria.Query), available.Count, matching.Count, requiredMinimumDays, pricingFailures);
    }

    /// <summary>Gearbox and minimum seats filters of the query.</summary>
    internal static bool Matches(VehicleCategoryInfo category, SearchQuery query) =>
        query.Gearbox switch
        {
            GearboxFilter.Automatic => category.Transmission == TransmissionInfo.Automatic,
            GearboxFilter.Manual => category.Transmission == TransmissionInfo.Manual,
            _ => true,
        }
        && (query.MinimumSeats is not { } seats || category.Seats >= seats);

    /// <summary>By total price (then display order), the requested category first.</summary>
    internal static IReadOnlyList<SearchOffer> Order(IEnumerable<SearchOffer> offers, SearchQuery query)
    {
        var byPrice = query.Sort == ResultSort.PriceDescending
            ? offers.OrderByDescending(o => o.Total.Amount)
            : offers.OrderBy(o => o.Total.Amount);
        return
        [
            .. byPrice
                .ThenBy(o => o.Category.DisplayOrder)
                .ThenBy(o => o.Category.AcrissCode, StringComparer.Ordinal)
                .OrderBy(o => query.Category is { } requested && o.Category.AcrissCode == requested.Value ? 0 : 1),
        ];
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Available category {Category} could not be priced ({ErrorCode}): left out of the results.")]
    private partial void LogNotPriced(string category, string errorCode);
}

/// <summary>An available category with its price for the searched stay.</summary>
public sealed record SearchOffer(VehicleCategoryInfo Category, int Remaining, PriceQuote Quote)
{
    public Money Total => Quote.TotalInclVat;

    public int Days => Quote.Days;

    /// <summary>Total divided by the charged days, to the millime: the secondary price of the results.</summary>
    public Money PerDay => Money.Tnd(Quote.TotalInclVat.Amount / Math.Max(1, Quote.Days));
}

/// <summary>
/// Result of a search: the priced offers in display order, how many categories were available before and after the
/// filters, the minimum duration required when the stay is too short, and the categories that could not be priced.
/// </summary>
public sealed record SearchOutcome(
    IReadOnlyList<SearchOffer> Offers,
    int AvailableCount,
    int MatchingCount,
    int? RequiredMinimumDays,
    int PricingFailures)
{
    public SearchOutcomeKind Kind => this switch
    {
        { Offers.Count: > 0 } => SearchOutcomeKind.Offers,
        { AvailableCount: 0 } => SearchOutcomeKind.NoAvailability,
        { MatchingCount: 0 } => SearchOutcomeKind.NoMatch,
        { RequiredMinimumDays: not null } => SearchOutcomeKind.MinimumDuration,
        _ => SearchOutcomeKind.PricingUnavailable,
    };
}

public enum SearchOutcomeKind
{
    Offers,
    NoAvailability,
    NoMatch,
    MinimumDuration,
    PricingUnavailable,
}
