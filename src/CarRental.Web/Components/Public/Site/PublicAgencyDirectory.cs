using CarRental.Application.Customers.Contracts;
using CarRental.Application.Fleet.Contracts;
using CarRental.Application.Pricing.Contracts;
using CarRental.Application.Referential.Contracts;
using CarRental.Application.Reporting.Contracts;
using CarRental.Domain.Common;
using Microsoft.Extensions.Caching.Memory;

namespace CarRental.Web.Components.Public.Site;

/// <summary>
/// What the site of an agency shows about it (ADR-0067), read from the owning modules and kept in memory per agency for
/// <see cref="CacheDuration"/>: agency, pick-up locations, opening hours, active categories, euro rate, eligibility and
/// retention settings, and the range of the published daily prices. A change made in the back office reaches the
/// site within that delay; prices and availability of a search are never taken from here.
/// </summary>
/// <remarks>
/// Read by a request of that agency's site (the visitor works for that agency only): each entry is loaded by a visitor of
/// its own agency and found again by its key only.
/// </remarks>
public sealed partial class PublicAgencyDirectory(
    IReferentialQueries referential,
    IFleetQueries fleet,
    IPricingQueries pricing,
    ISettingsReader settings,
    IMemoryCache cache,
    ILogger<PublicAgencyDirectory> logger)
{
    public static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The facts of the site of <paramref name="site"/>, or <see langword="null"/> when its agency cannot be read (it was
    /// just suspended).
    /// </summary>
    public async Task<PublicAgency?> GetAsync(AgencySummary site, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(site);
        var key = $"public-site:agency:{site.Id:N}";
        if (cache.TryGetValue(key, out PublicAgency? cached) && (cached is null || cached.Agency.Id == site.Id))
        {
            return cached is null ? null : cached with { Site = site };
        }

        var loaded = await LoadAsync(site, cancellationToken);
        cache.Set(key, loaded, new MemoryCacheEntryOptions
        {
            Size = 1,
            AbsoluteExpirationRelativeToNow = loaded is null ? TimeSpan.FromMinutes(1) : CacheDuration,
        });
        return loaded;
    }

    private async Task<PublicAgency?> LoadAsync(AgencySummary site, CancellationToken cancellationToken)
    {
        var agency = (await referential.GetAgenciesAsync(cancellationToken)).FirstOrDefault(a => a.Id == site.Id && a.IsActive);
        if (agency is null)
        {
            LogNoAgency(site.Slug);
            return null;
        }

        var locations = await referential.GetLocationsAsync(agency.Id, cancellationToken);
        var categories = await fleet.GetCategoriesAsync(agency.Id, includeInactive: false, cancellationToken);
        var plan = await pricing.GetPublishedPlanAsync(agency.Id, channel: null, cancellationToken);
        return new PublicAgency(
            site,
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "The agency of the site {Slug} cannot be read: its pages show no agency data.")]
    private partial void LogNoAgency(string slug);
}

/// <summary>The agency presented by its public site (<paramref name="Site"/>: its address, name and modules) and its public facts.</summary>
public sealed record PublicAgency(
    AgencySummary Site,
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
