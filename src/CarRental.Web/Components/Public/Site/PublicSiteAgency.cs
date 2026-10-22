using CarRental.Application.Referential.Contracts;
using CarRental.Domain.Common;
using CarRental.Web.Hosting;
using CarRental.Web.Localization;
using Microsoft.AspNetCore.Localization;

namespace CarRental.Web.Components.Public.Site;

/// <summary>
/// The agency whose public site a request shows (ADR-0067). The first segment of every public address is the address of
/// an agency's site (<c>Agency.Slug</c>): <c>/{agency}/{culture}/…</c>, e.g. <c>/agence-demo-tunis/fr/reserver</c>. The
/// public pages, their forms and their APIs work for that agency only, as a visitor, whether or not the browser carries
/// a staff session. Resolved once per request by <see cref="PublicSiteAgencyMiddleware"/> from the route value
/// <see cref="RouteParameter"/>; read by the ambient user context, the pages and the endpoints.
/// </summary>
/// <remarks>
/// Addresses of the whole platform stay at the root: <c>/</c> (the platform's own page), <c>/admin</c>, <c>/account</c>,
/// <c>/media/categories/{id}</c>, <c>/health</c>, <c>/api/channels/{code}</c>, the WhatsApp webhook, the return of the
/// payment gateway, the static files, <c>/sitemap.xml</c> and <c>/robots.txt</c>; their first segments are reserved
/// words that no agency can take (<see cref="Domain.Referential.AgencySlug.Reserved"/>).
/// </remarks>
public static class PublicSiteAgency
{
    /// <summary>The route parameter of the agency in every public address.</summary>
    public const string RouteParameter = "agency";

    /// <summary>The first segment of every public route: the address of the agency's site.</summary>
    public const string RouteParameterSegment = "/{" + RouteParameter + "}";

    /// <summary>The start of every public page: the agency, then the culture (ADR-0015, ADR-0067).</summary>
    public const string RoutePrefix = RouteParameterSegment + "/{culture:length(2)}";

    private const string ItemKey = "carrental.public-site-agency";

    /// <summary>The agency of the public site of this request, <see langword="null"/> outside the public site.</summary>
    public static Guid? Of(HttpContext context) => SiteOf(context)?.Id;

    /// <summary>The agency of the public site of this request (address, name, modules), <see langword="null"/> outside the public site.</summary>
    public static AgencySummary? SiteOf(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Items.TryGetValue(ItemKey, out var value) && value is AgencySummary agency ? agency : null;
    }

    /// <summary>The agency of the public site of this request; only for the pages and endpoints of the public site.</summary>
    public static AgencySummary Require(HttpContext context) =>
        SiteOf(context) ?? throw new InvalidOperationException("This request is not an address of a public site (/{agency}/{culture}/…).");

    /// <summary>The canonical address of the site of this request (<c>agence-1</c>), first segment of its links.</summary>
    public static string SlugOf(HttpContext context) => Require(context).Slug;

    /// <summary>
    /// The WhatsApp number of the site's button and links: the platform number (<c>PublicSite:WhatsAppNumber</c>, the
    /// partner accounts are the platform's, ADR-0067), only when the agency's WhatsApp module is on.
    /// </summary>
    public static string? WhatsAppNumber(AgencySummary? agency, PublicSiteOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return agency is not null && agency.HasModule(AgencyModules.WhatsApp) && !string.IsNullOrWhiteSpace(options.WhatsAppNumber)
            ? options.WhatsAppNumber
            : null;
    }

    internal static void Set(HttpContext context, AgencySummary agency) => context.Items[ItemKey] = agency;
}

/// <summary>
/// Resolves the agency of a public address before anything reads the actor of the request: the route value
/// <see cref="PublicSiteAgency.RouteParameter"/> through the cached <see cref="IAgencyDirectory"/>. An unknown address, a
/// suspended agency or a site whose module is off answer 404 (the status code page then renders, as for any 404).
/// </summary>
internal sealed class PublicSiteAgencyMiddleware(RequestDelegate next, IAgencyDirectory agencies)
{
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.GetRouteValue(PublicSiteAgency.RouteParameter) is string slug)
        {
            var agency = await agencies.FindBySlugAsync(slug, context.RequestAborted);
            if (agency is not { HasOpenPublicSite: true })
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            PublicSiteAgency.Set(context, agency);
        }

        await next(context);
    }
}

/// <summary>
/// <c>/{agency}</c>: the site of the agency in the remembered or preferred culture (cookie, then <c>Accept-Language</c>,
/// else French), like the root once did (ADR-0015).
/// </summary>
internal static class PublicSiteRoot
{
    public static IResult Redirect(HttpContext context, CultureCatalog cultures)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(cultures);
        var requested = context.Features.Get<IRequestCultureFeature>()?.RequestCulture.UICulture.TwoLetterISOLanguageName;
        var culture = cultures.IsEnabled(requested) ? requested! : CultureCatalog.Default;
        context.Response.Headers.Vary = "Accept-Language, Cookie";
        return Results.Redirect(PublicRoutes.PathFor(PublicSiteAgency.Require(context).Slug, PublicPage.Home, culture));
    }
}
