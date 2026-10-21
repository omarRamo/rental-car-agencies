using System.Security.Cryptography;
using System.Text;
using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Pricing.Contracts;
using CarRental.Domain.Common;
using CarRental.Web.Components.Public;
using CarRental.Web.Components.Public.Search;
using CarRental.Web.Components.Public.Site;
using CarRental.Web.Hosting;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;

namespace CarRental.Web.Endpoints.PublicSite;

/// <summary>Keys of the clients of the public quote API (the WhatsApp bot, a partner site): <c>PublicApi:QuoteApiKeys</c>.</summary>
public sealed class PublicApiOptions
{
    public const string SectionName = "PublicApi";

    /// <summary>Accepted values of the <c>X-Api-Key</c> header (secrets: Key Vault <c>PublicApi--QuoteApiKeys--0</c>). None: the API refuses every call.</summary>
    public IList<string> QuoteApiKeys { get; } = [];
}

/// <summary>
/// The public quote API (SPEC §13, ADR-0049): <c>POST /api/public/quotes</c> prices a rental with the pricing engine and
/// locks the quote for 24 hours (same rules as the "Choose" button of the site), <c>GET /api/public/quotes/{code}</c>
/// reads a locked quote. The deep link of the answer (<c>devis=CODE</c>) opens the results of the same search; choosing the
/// category there reuses the locked quote while it is valid and matches. Clients authenticate with an API key
/// (<c>X-Api-Key</c>, compared in constant time); calls are rate limited by client address and never cached.
/// </summary>
public static class PublicQuoteApi
{
    public const string Route = "/api/public/quotes";

    public const string ApiKeyHeader = "X-Api-Key";

    /// <summary>Actor of the quotes locked through the API.</summary>
    public const string Actor = "system:public-api";

    public static IEndpointRouteBuilder MapPublicQuoteApi(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapPost(Route, CreateAsync)
            .AllowAnonymous()
            .DisableAntiforgery()
            .RequireRateLimiting(PublicRateLimits.Search)
            .WithName("CreatePublicQuote")
            .WithTags("Public")
            .WithSummary("Prices a rental and locks the quote for 24 hours (API key).");
        endpoints.MapGet($"{Route}/{{code}}", GetAsync)
            .AllowAnonymous()
            .RequireRateLimiting(PublicRateLimits.Search)
            .WithName("GetPublicQuote")
            .WithTags("Public")
            .WithSummary("A locked quote by its code (API key).");
        return endpoints;
    }

    internal static async Task<Results<Created<PublicQuoteResponse>, ValidationProblem, ProblemHttpResult>> CreateAsync(
        PublicQuoteRequest? request,
        HttpContext context,
        PublicAgencyDirectory directory,
        TimeProvider timeProvider,
        IOptions<PublicApiOptions> options,
        IOptions<PublicSiteOptions> site,
        CancellationToken cancellationToken)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (!IsAuthorized(context.Request, options.Value))
        {
            return Unauthorized();
        }

        if (request is null)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["body"] = ["required"] }, title: "The request is invalid.");
        }

        var agency = await directory.GetAsync(cancellationToken);
        if (agency is null)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "No agency is open for online booking yet.");
        }

        var query = request.ToQuery();
        var validation = SearchValidator.Validate(query, agency.Locations, timeProvider.GetUtcNow());
        if (validation.Criteria is not { } criteria)
        {
            return TypedResults.ValidationProblem(
                validation.Errors.GroupBy(e => e.Field).ToDictionary(g => g.Key, g => g.Select(e => e.Code).ToArray()),
                title: "The search is invalid.");
        }

        var category = agency.Categories.FirstOrDefault(c => string.Equals(c.AcrissCode, query.Category?.Value, StringComparison.Ordinal));
        if (category is null)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["category"] = ["category_unknown"] }, title: "The category is unknown.");
        }

        await using var scope = context.RequestServices.GetRequiredService<IServiceScopeFactory>().CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<CarRental.Application.Common.Security.ScopedUserContext>()
            .Initialize(CarRental.Application.Common.Security.UserContextSnapshot.System(Actor, Security.CorrelationIds.Current(context)));
        var requests = scope.ServiceProvider.GetRequiredService<IBookingRequests>();
        var locked = await requests.CreateLockedQuoteAsync(
            new PriceRequest(
                agency.Agency.Id,
                category.Id,
                criteria.PickupLocation.Id,
                criteria.ReturnLocation.Id,
                criteria.PickupAt,
                criteria.ReturnAt,
                criteria.Channel,
                [.. (request.Extras ?? []).Where(e => !string.IsNullOrWhiteSpace(e.Code)).Take(20).Select(e => new SelectedExtra(e.Code!.Trim().ToUpperInvariant(), e.Quantity))]),
            cancellationToken);
        if (locked.IsFailure)
        {
            return TypedResults.Problem(
                statusCode: locked.Error.Code.StartsWith("pricing.", StringComparison.Ordinal) || locked.Error.Code.StartsWith("booking.", StringComparison.Ordinal)
                    ? StatusCodes.Status422UnprocessableEntity
                    : StatusCodes.Status409Conflict,
                title: "The rental cannot be priced.",
                extensions: new Dictionary<string, object?> { ["code"] = locked.Error.Code });
        }

        var response = PublicQuoteResponse.From(locked.Value, agency, criteria.Query, PublicUrls.BaseUri(site.Value.BaseUrl, context.Request));
        return TypedResults.Created($"{Route}/{response.Code}", response);
    }

    internal static async Task<Results<Ok<PublicQuoteResponse>, NotFound, ProblemHttpResult>> GetAsync(
        string code,
        HttpContext context,
        PublicAgencyDirectory directory,
        IBookingRequests requests,
        IOptions<PublicApiOptions> options,
        IOptions<PublicSiteOptions> site,
        CancellationToken cancellationToken)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (!IsAuthorized(context.Request, options.Value))
        {
            return Unauthorized();
        }

        var agency = await directory.GetAsync(cancellationToken);
        var quote = agency is null ? null : await requests.GetLockedQuoteAsync(code, cancellationToken);
        if (agency is null || quote is null || quote.AgencyId != agency.Agency.Id)
        {
            return TypedResults.NotFound();
        }

        var pickup = agency.Locations.FirstOrDefault(l => l.Id == quote.PickupLocationId);
        var dropOff = agency.Locations.FirstOrDefault(l => l.Id == quote.ReturnLocationId);
        var category = agency.Categories.FirstOrDefault(c => c.Id == quote.CategoryId);
        var query = SearchQuery.Empty with
        {
            PickupLocation = pickup?.Code,
            ReturnLocation = dropOff is not null && dropOff.Id != pickup?.Id ? dropOff.Code : null,
            Pickup = SearchValidator.FormatLocal(quote.PickupAt),
            Return = SearchValidator.FormatLocal(quote.ReturnAt),
            Category = category is null ? null : AcrissCode.Create(category.AcrissCode).Value,
            Traffic = quote.Channel == SalesChannel.WhatsApp ? new TrafficSource(TrafficSource.WhatsAppSource) : TrafficSource.Direct,
        };
        return TypedResults.Ok(PublicQuoteResponse.From(quote, agency, query, PublicUrls.BaseUri(site.Value.BaseUrl, context.Request)));
    }

    /// <summary>Whether the request carries one of the configured keys (constant-time comparison of their hashes).</summary>
    internal static bool IsAuthorized(HttpRequest request, PublicApiOptions options)
    {
        var provided = request.Headers[ApiKeyHeader].ToString();
        if (string.IsNullOrEmpty(provided) || provided.Length > 256)
        {
            return false;
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(provided));
        var authorized = false;
        foreach (var key in options.QuoteApiKeys.Where(k => !string.IsNullOrWhiteSpace(k)))
        {
            authorized |= CryptographicOperations.FixedTimeEquals(hash, SHA256.HashData(Encoding.UTF8.GetBytes(key.Trim())));
        }

        return authorized;
    }

    private static ProblemHttpResult Unauthorized() =>
        TypedResults.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "A valid API key is required (X-Api-Key).");
}

/// <summary>
/// A rental to price and lock, named like the deep links: pick-up location code, optional return location code, pick-up
/// and return in Tunis time (<c>2027-07-01T10:00</c>), ACRISS category, <c>source</c> (<c>whatsapp</c> prices the WhatsApp
/// channel) and the selectable extras with their quantity.
/// </summary>
public sealed record PublicQuoteRequest(
    string? PickupLocation,
    string? ReturnLocation,
    string? Pickup,
    string? Return,
    string? Category,
    string? Source,
    IReadOnlyList<PublicQuoteExtra>? Extras = null)
{
    internal SearchQuery ToQuery() => SearchQuery.Parse(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>
    {
        [SearchParameters.PickupLocation] = PickupLocation,
        [SearchParameters.ReturnLocation] = ReturnLocation,
        [SearchParameters.Pickup] = Pickup,
        [SearchParameters.Return] = Return,
        [SearchParameters.Category] = Category,
        [SearchParameters.Source] = Source,
    });
}

/// <summary>A selectable extra (code of the rate plan) and its quantity.</summary>
public sealed record PublicQuoteExtra(string? Code, int Quantity);

/// <summary>A locked quote: its code, until when it holds, its total and the deep link that books it.</summary>
/// <param name="Code">The quote code (<c>devis</c> of a deep link).</param>
/// <param name="ExpiresAtUtc">End of the price guarantee.</param>
/// <param name="Expired">The guarantee is over (a new quote is needed).</param>
/// <param name="Category">ACRISS code.</param>
/// <param name="PickupLocation">Pick-up location code.</param>
/// <param name="ReturnLocation">Return location code.</param>
/// <param name="PickupAt">Pick-up, with the Tunis offset.</param>
/// <param name="ReturnAt">Return, with the Tunis offset.</param>
/// <param name="Channel"><c>web</c> or <c>whatsapp</c>.</param>
/// <param name="Days">Charged days.</param>
/// <param name="TotalInclVat">Total TTC in TND (VAT and daily contribution included).</param>
/// <param name="Deposit">Deposit in TND (not part of the total).</param>
/// <param name="Currency">Always <c>TND</c>.</param>
/// <param name="Extras">The extras priced.</param>
/// <param name="BookingUrls">Absolute deep links that book the quote, by language (<c>fr</c>, <c>en</c>).</param>
public sealed record PublicQuoteResponse(
    string Code,
    DateTimeOffset ExpiresAtUtc,
    bool Expired,
    string? Category,
    string? PickupLocation,
    string? ReturnLocation,
    DateTimeOffset PickupAt,
    DateTimeOffset ReturnAt,
    string Channel,
    int Days,
    decimal TotalInclVat,
    decimal Deposit,
    string Currency,
    IReadOnlyList<PublicQuoteExtra> Extras,
    IReadOnlyDictionary<string, string> BookingUrls)
{
    private static readonly string[] LinkCultures = ["fr", "en"];

    internal static PublicQuoteResponse From(LockedQuoteInfo quote, PublicAgency agency, SearchQuery query, Uri baseUri)
    {
        var linkQuery = query with { QuoteCode = quote.Code, Info = null };
        var urls = LinkCultures.ToDictionary(
            culture => culture,
            culture => PublicUrls.Absolute(baseUri, PublicRoutes.PathFor(PublicPage.Results, culture) + linkQuery.ToQueryString()).AbsoluteUri);
        return new PublicQuoteResponse(
            quote.Code,
            quote.ExpiresAtUtc,
            quote.IsExpired,
            agency.Categories.FirstOrDefault(c => c.Id == quote.CategoryId)?.AcrissCode,
            agency.Locations.FirstOrDefault(l => l.Id == quote.PickupLocationId)?.Code,
            agency.Locations.FirstOrDefault(l => l.Id == quote.ReturnLocationId)?.Code,
            TunisTime.ToTunis(quote.PickupAt),
            TunisTime.ToTunis(quote.ReturnAt),
            quote.Channel == SalesChannel.WhatsApp ? "whatsapp" : "web",
            quote.Days,
            quote.TotalInclVat.Amount,
            quote.Deposit.Amount,
            Domain.Common.Currency.Tnd.Code,
            [.. quote.Extras.Select(e => new PublicQuoteExtra(e.Code, e.Quantity))],
            urls);
    }
}
