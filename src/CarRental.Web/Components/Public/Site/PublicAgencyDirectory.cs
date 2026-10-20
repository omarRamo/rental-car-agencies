using CarRental.Application.Customers.Contracts;
using CarRental.Application.Fleet.Contracts;
using CarRental.Application.Pricing.Contracts;
using CarRental.Application.Referential.Contracts;
using CarRental.Application.Reporting.Contracts;
using CarRental.Domain.Common;
using CarRental.Web.Hosting;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace CarRental.Web.Components.Public.Site;

/// <summary>
/// What the public site shows about its agency, read from the owning modules and kept in memory for
/// <see cref="CacheDuration"/>: agency, pick-up locations, opening hours, active categories, euro rate, eligibility and
/// retention settings, and the range of the published daily prices. A change made in the back office reaches the
/// site within that delay; prices and availability of a search are never taken from here.
/// </summary>
public sealed partial class PublicAgencyDirectory(
    IReferentialQueries referential,
    IFleetQueries fleet,
    IPricingQueries pricing,
    ISettingsReader settings,
    IMemoryCache cache,
    IOptions<PublicSiteOptions> options,
    ILogger<PublicAgencyDirectory> logger)
{
    public static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);

    private const string CacheKey = "public-site:agency";

    /// <summary>The agency of the site, or <see langword="null"/> when none is set up yet (fresh installation).</summary>
    public async Task<PublicAgency?> GetAsync(CancellationToken cancellationToken = default)
    {
        if (cache.TryGetValue(CacheKey, out PublicAgency? cached))
        {
            return cached;
        }

        var loaded = await LoadAsync(cancellationToken);
        cache.Set(CacheKey, loaded, loaded is null ? TimeSpan.FromMinutes(1) : CacheDuration);
        return loaded;
    }

    private async Task<PublicAgency?> LoadAsync(CancellationToken cancellationToken)
    {
        var agencies = (await referential.GetAgenciesAsync(cancellationToken)).Where(a => a.IsActive).ToList();
        var agency = Select(agencies, options.Value.AgencyId);
        if (agency is null)
        {
            LogNoAgency();
            return null;
        }

        var locations = await referential.GetLocationsAsync(agency.Id, cancellationToken);
        var categories = await fleet.GetCategoriesAsync(agency.Id, includeInactive: false, cancellationToken);
        var plan = await pricing.GetPublishedPlanAsync(agency.Id, channel: null, cancellationToken);
        return new PublicAgency(
            agency,
            locations,
            categories,
            await settings.GetAsync(PricingSettings.AgencyOpeningHours, agency.Id, cancellationToken),
            await settings.GetAsync(PublicSiteSettings.EurExchangeRate, agency.Id, cancellationToken),
            new PublicPolicies(
                await settings.GetAsync(CustomerSettings.DriverMinimumAge, agency.Id, cancellationToken),
                await settings.GetAsync(CustomerSettings.DriverMinimumLicenseYears, agency.Id, cancellationToken),
                await settings.GetAsync(PricingSettings.YoungDriverAgeBelow, agency.Id, cancellationToken),
                await settings.GetAsync(ReportingSettings.FunnelEventRetentionDays, agency.Id, cancellationToken),
                await settings.GetAsync(CustomerSettings.DocumentImageRetentionEnabled, agency.Id, cancellationToken),
                await settings.GetAsync(CustomerSettings.DocumentImageRetentionDays, agency.Id, cancellationToken)),
            DailyPriceRange(plan));
    }

    /// <summary>The configured agency, else the only active one, else the first one created (identifiers are time-ordered).</summary>
    internal static AgencyInfo? Select(IReadOnlyList<AgencyInfo> activeAgencies, Guid? configuredId) =>
        configuredId is { } id
            ? activeAgencies.FirstOrDefault(a => a.Id == id)
            : activeAgencies.OrderBy(a => a.Id).FirstOrDefault();

    /// <summary>Lowest and highest daily price TTC of the published grid (all categories and periods).</summary>
    internal static MoneyRange? DailyPriceRange(RatePlanSummary? plan)
    {
        var prices = plan?.CategoryRates
            .SelectMany(rate => new Money?[] { rate.BasePrice, rate.LowSeasonPrice, rate.MidSeasonPrice, rate.HighSeasonPrice }
                .Concat(rate.EventPrices.Values.Select(price => (Money?)price)))
            .OfType<Money>()
            .Where(price => price.IsPositive)
            .ToList();
        return prices is { Count: > 0 } ? new MoneyRange(prices.Min(), prices.Max()) : null;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "No active agency (or PublicSite:AgencyId does not match one): the public site shows no agency data.")]
    private partial void LogNoAgency();
}

/// <summary>The agency presented by the public site and its public facts.</summary>
public sealed record PublicAgency(
    AgencyInfo Agency,
    IReadOnlyList<LocationInfo> Locations,
    IReadOnlyList<VehicleCategoryInfo> Categories,
    OpeningHours OpeningHours,
    decimal EurExchangeRate,
    PublicPolicies Policies,
    MoneyRange? DailyPriceRange)
{
    /// <summary>The agency office when there is one (quotes of the home page, pre-selected pick-up), else the first location.</summary>
    public LocationInfo? MainLocation =>
        Locations.FirstOrDefault(l => l.Kind == LocationKindInfo.AgencyOffice) ?? (Locations.Count > 0 ? Locations[0] : null);

    public LocationInfo? FindLocation(string? code) =>
        string.IsNullOrWhiteSpace(code)
            ? null
            : Locations.FirstOrDefault(l => string.Equals(l.Code, code.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Lowest and highest deposit of the categories (FAQ).</summary>
    public MoneyRange? DepositRange =>
        Categories.Count == 0 ? null : new MoneyRange(Categories.Min(c => c.Deposit), Categories.Max(c => c.Deposit));
}

/// <summary>Rules quoted by the FAQ and the privacy page, read from the business settings.</summary>
public sealed record PublicPolicies(
    int DriverMinimumAge,
    int DriverMinimumLicenseYears,
    int YoungDriverAgeBelow,
    int FunnelEventRetentionDays,
    bool DocumentImageRetentionEnabled,
    int DocumentImageRetentionDays);

/// <summary>An inclusive range of amounts.</summary>
public sealed record MoneyRange(Money Minimum, Money Maximum);
