using CarRental.Application.Fleet.Contracts;
using CarRental.Application.Referential.Contracts;
using CarRental.Domain.Common;
using CarRental.Web.Components.Public.Search;
using CarRental.Web.Components.Public.Site;
using CarRental.Web.Hosting;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;

namespace CarRental.Web.Endpoints.PublicSite;

/// <summary>
/// <c>GET /api/public/availability</c> (SPEC §13): the categories still available for a location and dates, with their
/// remaining count and total price TTC, for the site and the WhatsApp bot. Same parameters and rules as the results
/// page (<c>lieu</c>, <c>retour_lieu</c>, <c>depart</c>, <c>retour</c> in Tunis time, <c>src</c>, <c>boite</c>,
/// <c>places</c>); anonymous, rate-limited (<see cref="PublicRateLimits.Search"/>), served from the cached availability
/// engine and the pricing engine, never cached by HTTP.
/// </summary>
public static class PublicAvailabilityApi
{
    public const string Route = "/api/public/availability";

    public static IEndpointRouteBuilder MapPublicAvailabilityApi(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapGet(Route, GetAsync)
            .AllowAnonymous()
            .RequireRateLimiting(PublicRateLimits.Search)
            .WithName("GetPublicAvailability")
            .WithTags("Public")
            .WithSummary("Available categories and total prices TTC for a pick-up location and dates (Tunis time).");
        return endpoints;
    }

    internal static async Task<Results<Ok<PublicAvailabilityResponse>, ValidationProblem, ProblemHttpResult>> GetAsync(
        HttpContext context,
        PublicAgencyDirectory directory,
        PublicSearchService search,
        TimeProvider timeProvider,
        IOptions<PublicSiteOptions> site,
        CancellationToken cancellationToken)
    {
        context.Response.Headers.CacheControl = "no-store";
        var agency = await directory.GetAsync(cancellationToken);
        if (agency is null)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "No agency is open for online search yet.");
        }

        var validation = SearchValidator.Validate(SearchQuery.Parse(context.Request.Query), agency.Locations, timeProvider.GetUtcNow());
        if (validation.Criteria is not { } criteria)
        {
            return TypedResults.ValidationProblem(
                validation.Errors.GroupBy(e => e.Field).ToDictionary(g => g.Key, g => g.Select(e => e.Code).ToArray()),
                title: "The search is invalid.");
        }

        var outcome = await search.SearchAsync(criteria, cancellationToken);
        var baseUri = PublicUrls.BaseUri(site.Value.BaseUrl, context.Request);
        return TypedResults.Ok(PublicAvailabilityResponse.From(criteria, outcome, agency.EurExchangeRate, baseUri));
    }
}

/// <summary>Answer of <c>GET /api/public/availability</c>; amounts in TND (VAT and daily contribution included).</summary>
/// <param name="PickupLocation">Pick-up location of the search.</param>
/// <param name="ReturnLocation">Return location (the pick-up location when not given).</param>
/// <param name="PickupAt">Pick-up, with the Tunis offset.</param>
/// <param name="ReturnAt">Return, with the Tunis offset.</param>
/// <param name="Channel"><c>web</c>, or <c>whatsapp</c> for <c>src=whatsapp</c>: the channel the prices are for.</param>
/// <param name="Currency">Always <c>TND</c>.</param>
/// <param name="RequiredMinimumDays">Set when the stay is shorter than the minimum duration of its season (no category then).</param>
/// <param name="Categories">Available categories matching the filters, cheapest first.</param>
public sealed record PublicAvailabilityResponse(
    PublicLocationDto PickupLocation,
    PublicLocationDto ReturnLocation,
    DateTimeOffset PickupAt,
    DateTimeOffset ReturnAt,
    string Channel,
    string Currency,
    int? RequiredMinimumDays,
    IReadOnlyList<PublicCategoryAvailabilityDto> Categories)
{
    internal static PublicAvailabilityResponse From(SearchCriteria criteria, SearchOutcome outcome, decimal eurRate, Uri baseUri) => new(
        PublicLocationDto.From(criteria.PickupLocation),
        PublicLocationDto.From(criteria.ReturnLocation),
        TunisTime.ToTunis(criteria.PickupAt),
        TunisTime.ToTunis(criteria.ReturnAt),
        criteria.Channel == SalesChannel.WhatsApp ? "whatsapp" : "web",
        Domain.Common.Currency.Tnd.Code,
        outcome.RequiredMinimumDays,
        [.. outcome.Offers.Select(offer => PublicCategoryAvailabilityDto.From(offer, eurRate, baseUri))]);
}

/// <summary>A pick-up or return location: code (deep links), name and kind (<c>agency</c>, <c>airport</c>, <c>delivery</c>).</summary>
public sealed record PublicLocationDto(string Code, string Name, string Kind)
{
    internal static PublicLocationDto From(LocationInfo location) => new(
        location.Code,
        location.Name,
        location.Kind switch
        {
            LocationKindInfo.Airport => "airport",
            LocationKindInfo.DeliveryPoint => "delivery",
            _ => "agency",
        });
}

/// <summary>An available category: features, vehicles left for the period, price for the stay and deposit.</summary>
/// <param name="Code">ACRISS code, e.g. <c>ECMR</c> (the <c>cat</c> of a deep link).</param>
/// <param name="Name">Name of the category.</param>
/// <param name="ExampleModel">Model shown "or similar".</param>
/// <param name="Seats">Seats.</param>
/// <param name="Doors">Doors.</param>
/// <param name="LargeBags">Large suitcases.</param>
/// <param name="SmallBags">Small bags.</param>
/// <param name="Transmission"><c>manual</c> or <c>automatic</c>.</param>
/// <param name="Fuel"><c>petrol</c>, <c>diesel</c>, <c>hybrid</c>, <c>electric</c> or <c>lpg</c>.</param>
/// <param name="AirConditioning">Air conditioning.</param>
/// <param name="PhotoUrl">Absolute address of the public photo, or <see langword="null"/>.</param>
/// <param name="Remaining">How many more bookings of this period the category can take.</param>
/// <param name="Days">Charged days.</param>
/// <param name="TotalInclVat">Total TTC of the stay, without extras.</param>
/// <param name="PricePerDay">Total divided by the charged days.</param>
/// <param name="Deposit">Deposit of the category (not part of the total).</param>
/// <param name="EurEquivalent">Indicative euro equivalent of the total, rounded to the euro.</param>
public sealed record PublicCategoryAvailabilityDto(
    string Code,
    string Name,
    string ExampleModel,
    int Seats,
    int Doors,
    int LargeBags,
    int SmallBags,
    string Transmission,
    string Fuel,
    bool AirConditioning,
    string? PhotoUrl,
    int Remaining,
    int Days,
    decimal TotalInclVat,
    decimal PricePerDay,
    decimal Deposit,
    decimal? EurEquivalent)
{
    internal static PublicCategoryAvailabilityDto From(SearchOffer offer, decimal eurRate, Uri baseUri)
    {
        var category = offer.Category;
        return new PublicCategoryAvailabilityDto(
            category.AcrissCode,
            category.Name,
            category.ExampleModel,
            category.Seats,
            category.Doors,
            category.LargeBags,
            category.SmallBags,
            category.Transmission == TransmissionInfo.Automatic ? "automatic" : "manual",
            category.Fuel switch
            {
                FuelTypeInfo.Diesel => "diesel",
                FuelTypeInfo.Hybrid => "hybrid",
                FuelTypeInfo.Electric => "electric",
                FuelTypeInfo.Lpg => "lpg",
                _ => "petrol",
            },
            category.AirConditioning,
            category.PhotoUrl is { } photo ? PublicUrls.Absolute(baseUri, photo).AbsoluteUri : null,
            offer.Remaining,
            offer.Days,
            offer.Total.Amount,
            offer.PerDay.Amount,
            offer.Quote.Deposit.Amount,
            PublicFormat.EuroEquivalent(offer.Total, eurRate));
    }
}
