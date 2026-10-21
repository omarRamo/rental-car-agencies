using CarRental.Application.Fleet.Contracts;
using CarRental.Application.Pricing.Contracts;
using CarRental.Domain.Common;
using Microsoft.Extensions.Caching.Memory;

namespace CarRental.Web.Components.Public.Site;

/// <summary>
/// The categories shown on the home page with a "from" price per day: the total TTC that the pricing engine gives for
/// the next weekend (Saturday 10:00 to Monday 10:00, Tunis time, at the main location, web channel, no extra), extended
/// to the minimum duration when the season requires one, divided by the charged days. Kept in memory for
/// <see cref="CacheDuration"/> per agency and weekend: the home page stays cacheable and cheap, and no price is ever
/// written by hand.
/// </summary>
public sealed partial class FeaturedOffers(
    IPricingService pricing,
    IMemoryCache cache,
    TimeProvider timeProvider,
    ILogger<FeaturedOffers> logger)
{
    public static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(10);

    public static readonly TimeOnly PickupTime = new(10, 0);

    private const int MaxCategories = 6;

    public async Task<FeaturedWeekend> GetAsync(PublicAgency agency, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(agency);
        var saturday = NextSaturday(TunisTime.Today(timeProvider));
        var key = $"public-site:featured:{agency.Agency.Id:N}:{saturday:yyyyMMdd}";
        if (cache.TryGetValue(key, out FeaturedWeekend? cached) && cached is not null)
        {
            return cached;
        }

        var weekend = await PriceAsync(agency, saturday, cancellationToken);
        cache.Set(key, weekend, CacheDuration);
        return weekend;
    }

    /// <summary>The first Saturday strictly after <paramref name="today"/> (a weekend that can still be booked).</summary>
    internal static DateOnly NextSaturday(DateOnly today)
    {
        var days = ((int)DayOfWeek.Saturday - (int)today.DayOfWeek + 7) % 7;
        return today.AddDays(days == 0 ? 7 : days);
    }

    private async Task<FeaturedWeekend> PriceAsync(PublicAgency agency, DateOnly saturday, CancellationToken cancellationToken)
    {
        var pickupAt = TunisTime.FromLocal(saturday, PickupTime);
        var weekendReturn = TunisTime.FromLocal(saturday.AddDays(2), PickupTime);
        var location = agency.MainLocation;
        var offers = new List<FeaturedOffer>();
        foreach (var category in agency.Categories.Take(MaxCategories))
        {
            offers.Add(location is null
                ? new FeaturedOffer(category, null, 0, weekendReturn)
                : await PriceCategoryAsync(agency, category, location.Id, pickupAt, weekendReturn, cancellationToken));
        }

        return new FeaturedWeekend(location?.Code, pickupAt, weekendReturn, offers);
    }

    private async Task<FeaturedOffer> PriceCategoryAsync(
        PublicAgency agency,
        VehicleCategoryInfo category,
        Guid locationId,
        DateTimeOffset pickupAt,
        DateTimeOffset returnAt,
        CancellationToken cancellationToken)
    {
        var request = new PriceRequest(agency.Agency.Id, category.Id, locationId, locationId, pickupAt, returnAt, SalesChannel.Web);
        var quote = await pricing.QuoteAsync(request, cancellationToken);
        if (quote.IsFailure && PricingErrorCodes.TryGetRequiredMinimumDays(quote.Error, out var minimumDays))
        {
            returnAt = pickupAt.AddDays(minimumDays);
            quote = await pricing.QuoteAsync(request with { ReturnAt = returnAt }, cancellationToken);
        }

        if (quote.IsFailure)
        {
            LogNotPriced(category.AcrissCode, quote.Error.Code);
            return new FeaturedOffer(category, null, 0, returnAt);
        }

        var days = Math.Max(1, quote.Value.Days);
        return new FeaturedOffer(category, Money.Tnd(quote.Value.TotalInclVat.Amount / days), days, returnAt);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Category {Category} has no price for the next weekend ({ErrorCode}): shown without price on the home page.")]
    private partial void LogNotPriced(string category, string errorCode);
}

/// <summary>The weekend the home page prices are computed for (location code and Tunis instants) and its offers.</summary>
public sealed record FeaturedWeekend(string? LocationCode, DateTimeOffset PickupAt, DateTimeOffset ReturnAt, IReadOnlyList<FeaturedOffer> Offers);

/// <summary>A category of the home page: price per day TTC for <see cref="Days"/> days, or <see langword="null"/> when not priced.</summary>
public sealed record FeaturedOffer(VehicleCategoryInfo Category, Money? PerDay, int Days, DateTimeOffset ReturnAt);
