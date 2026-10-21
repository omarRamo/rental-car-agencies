using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Bookings.PublicAccess;
using CarRental.Domain.Bookings.Access;
using CarRental.Web.Components.Public;
using CarRental.Web.Components.Public.Booking;
using CarRental.Web.Components.Public.Search;
using CarRental.Web.Components.Public.Site;
using Microsoft.AspNetCore.Antiforgery;

namespace CarRental.Web.Endpoints.PublicSite;

/// <summary>
/// The "Choose" action of the results page (ADR-0049): the posted search is parsed and validated again (never trusted),
/// the category is looked up among the active categories of the agency, then the booking funnel opens a session — the
/// locked quote of the deep link (<c>devis</c>) when it is valid, unexpired and matches the search exactly, otherwise a
/// new quote priced and locked for 24 hours — and the browser goes to step 1 (options) of its token.
/// </summary>
/// <remarks>
/// <para>Contract: <c>POST /{culture}/reserver/selection</c> (<see cref="PublicRoutes.SelectionRouteTemplate"/>), form
/// fields named like the deep link (<see cref="SearchParameters"/>): <c>lieu</c>, <c>retour_lieu</c>, <c>depart</c>,
/// <c>retour</c>, <c>cat</c>, <c>devis</c>, <c>src</c> and <c>utm_*</c>, plus <c>boite</c>, <c>places</c> and <c>tri</c>
/// to come back to the same results, and the antiforgery field.</para>
/// <para>A search that is no longer valid goes back to the results (which show why), a category that is not available
/// any more to the results of the same search with a notice. Rate limited as a submission (<see cref="PublicRateLimits.Submit"/>):
/// each choice prices and locks a quote. Availability is only re-checked under the lock at the submission.</para>
/// </remarks>
public static partial class BookingSelectionEndpoint
{
    public static IEndpointRouteBuilder MapBookingSelection(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapPost(PublicRoutes.SelectionRouteTemplate, HandleAsync)
            .WithMetadata(new RequireAntiforgeryTokenAttribute())
            .AllowAnonymous()
            .RequireRateLimiting(PublicRateLimits.Submit)
            .WithOrder(-1)
            .ExcludeFromDescription();
        return endpoints;
    }

    internal static async Task<IResult> HandleAsync(
        string culture,
        HttpContext context,
        PublicAgencyDirectory directory,
        TimeProvider clock,
        ILoggerFactory loggers)
    {
        if (await PublicForms.ReadAsync(context) is not { } form)
        {
            return Results.Empty;
        }

        var lang = culture.ToLowerInvariant();
        var query = SearchQuery.Parse(form);
        var results = PublicRoutes.PathFor(PublicPage.Results, lang);
        var agency = await directory.GetAsync(context.RequestAborted);
        if (agency is null)
        {
            return PublicForms.SeeOther(context, results);
        }

        var validation = SearchValidator.Validate(query, agency.Locations, clock.GetUtcNow());
        if (validation.Criteria is not { } criteria)
        {
            // The results page shows what is wrong with the search.
            return PublicForms.SeeOther(context, results + query.ToQueryString());
        }

        var category = agency.Categories.FirstOrDefault(c => string.Equals(c.AcrissCode, query.Category?.Value, StringComparison.Ordinal));
        if (category is null)
        {
            return PublicForms.SeeOther(context, ResultsWith(results, query with { Category = null }, SearchQuery.UnavailableInfo));
        }

        await using var scope = PublicActors.CreateScope(context, PublicActors.Funnel);
        var funnel = scope.ServiceProvider.GetRequiredService<FunnelService>();
        var started = await funnel.StartAsync(
            new FunnelStartRequest(
                agency.Agency.Id,
                lang,
                criteria.Channel,
                AttributionOf(query.Traffic),
                category.Id,
                category.AcrissCode,
                criteria.PickupLocation.Id,
                criteria.ReturnLocation.Id,
                criteria.PickupAt,
                criteria.ReturnAt,
                criteria.Query.ToQueryString(),
                query.QuoteCode),
            context.RequestAborted);
        if (started.IsFailure)
        {
            LogNotStarted(loggers.CreateLogger(typeof(BookingSelectionEndpoint).FullName!), category.AcrissCode, started.Error.Code);
            return PublicForms.SeeOther(context, ResultsWith(results, query, SearchQuery.UnavailableInfo));
        }

        return PublicForms.SeeOther(context, BookingRoutes.Step(lang, started.Value.Token, FunnelStep.Options));
    }

    /// <summary>The source and UTM tags of the deep link, kept with the request (attribution of the booking).</summary>
    internal static RequestAttributionInfo? AttributionOf(TrafficSource traffic) =>
        traffic.Source is null && traffic.UtmSource is null && traffic.UtmMedium is null && traffic.UtmCampaign is null
        && traffic.UtmTerm is null && traffic.UtmContent is null
            ? null
            : new RequestAttributionInfo(traffic.Source, traffic.UtmSource, traffic.UtmMedium, traffic.UtmCampaign, traffic.UtmTerm, traffic.UtmContent);

    /// <summary>The results of <paramref name="query"/> with the notice <paramref name="info"/>.</summary>
    internal static string ResultsWith(string resultsPath, SearchQuery query, string info) =>
        resultsPath + QueryString.Create(query.ToParameters().Append(new KeyValuePair<string, string?>(SearchParameters.Info, info))).ToUriComponent();

    /// <summary>The search a funnel session was opened from (its canonical query) with a notice.</summary>
    internal static string SearchAgain(string culture, string? searchQuery, string info)
    {
        var results = PublicRoutes.PathFor(PublicPage.Results, culture);
        var query = string.IsNullOrEmpty(searchQuery) ? SearchQuery.Empty : SearchQuery.Parse(Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(searchQuery));
        return query.IsEmpty
            ? results + QueryString.Create(SearchParameters.Info, info).ToUriComponent()
            : ResultsWith(results, query, info);
    }

    /// <summary>Where a funnel address that cannot be used leads: the search (with why), or the confirmation of its request.</summary>
    internal static string Fallback(string culture, FunnelLookup lookup) => lookup.Failure switch
    {
        FunnelLookupFailure.Submitted when lookup.BookingNumber is { } number => BookingRoutes.Confirmation(culture, number),
        FunnelLookupFailure.Expired => SearchAgain(culture, lookup.SearchQuery, SearchQuery.ExpiredInfo),
        _ => SearchAgain(culture, null, SearchQuery.UnknownLinkInfo),
    };

    /// <summary>Where a funnel action refused for its session leads (<see langword="null"/>: the error belongs to the page).</summary>
    internal static async Task<string?> FallbackForAsync(string culture, string? token, string errorCode, FunnelService funnel, CancellationToken cancellationToken)
    {
        if (errorCode != FunnelErrors.SessionNotFound.Code && errorCode != FunnelErrors.SessionExpired.Code && errorCode != FunnelErrors.AlreadySubmitted.Code)
        {
            return null;
        }

        var lookup = await funnel.OpenAsync(token, cancellationToken: cancellationToken);
        return Fallback(culture, lookup.View is null ? lookup : FunnelLookup.NotFound);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "The funnel of category {Category} could not be opened ({ErrorCode}): back to the results.")]
    private static partial void LogNotStarted(ILogger logger, string category, string errorCode);
}
