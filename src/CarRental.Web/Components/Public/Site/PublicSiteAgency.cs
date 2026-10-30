using CarRental.Application.Messaging.Contracts;
using CarRental.Application.Referential.Contracts;
using CarRental.Domain.Referential;
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
/// <para>Addresses of the whole platform stay at the root: <c>/</c> (the platform's own page), <c>/admin</c>, <c>/account</c>,
/// <c>/media/categories/{id}</c>, <c>/health</c>, <c>/api/channels/{code}</c>, the WhatsApp webhook, the return of the
/// payment gateway, the static files, <c>/sitemap.xml</c> and <c>/robots.txt</c>; their first segments are reserved
/// words that no agency can take (<see cref="Domain.Referential.AgencySlug.Reserved"/>).</para>
/// <para>An agency's site may be served on the agency's own host name (ADR-0069, <see cref="AgencyHostMiddleware"/>):
/// the request <c>https://www.agence.tn/fr/reserver</c> is rewritten to <c>/{agency}/fr/reserver</c> before routing, and
/// every link written for the visitor leaves the address segment out (<see cref="LinkSlugOf"/>, <see cref="LinkRootOf"/>).</para>
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

    private const string WhatsAppKey = "carrental.public-site-whatsapp";

    private const string OwnHostKey = "carrental.public-site-own-host";

    private const string OwnHostSlugKey = "carrental.public-site-own-host-slug";

    private const string OriginalPathKey = "carrental.public-site-original-path";

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

    /// <summary>
    /// The first segment of the links of the site of this request: the canonical address of the site (<c>agence-1</c>) on
    /// the platform host, nothing (<see cref="string.Empty"/>) when the site is served on the agency's own host (ADR-0069):
    /// its links are then <c>/fr/…</c>. Only for the pages and endpoints of the public site.
    /// </summary>
    public static string SlugOf(HttpContext context) => IsOwnHost(context) ? string.Empty : Require(context).Slug;

    /// <summary>
    /// The first segment of the links of the site of this request, as <see cref="SlugOf"/>, or <see langword="null"/> outside
    /// any public site (the page of the platform, an unknown address).
    /// </summary>
    public static string? LinkSlugOf(HttpContext context) =>
        SiteOf(context) is { } site ? IsOwnHost(context) ? string.Empty : site.Slug : null;

    /// <summary>
    /// The start of every link of the site of this request: <c>/agence-1</c> on the platform host, nothing on the agency's
    /// own host; <see langword="null"/> outside any public site.
    /// </summary>
    public static string? LinkRootOf(HttpContext context) => LinkSlugOf(context) is { } slug ? PublicRoutes.Root(slug) : null;

    /// <summary>
    /// The host name of the request when it is one of the host names of an agency's own site (<c>www.agence.tn</c>,
    /// ADR-0069), whatever the address requested; <see langword="null"/> on the platform host.
    /// </summary>
    public static string? OwnHostOf(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Items.TryGetValue(OwnHostKey, out var value) && value is string host ? host : null;
    }

    /// <summary>True when the request came on the own host of an agency's site (ADR-0069).</summary>
    public static bool IsOwnHost(HttpContext context) => OwnHostOf(context) is not null;

    /// <summary>
    /// The path of the request as the visitor typed it: without the address segment added by the rewriting of the
    /// agency's own host (ADR-0069), the request path otherwise. For the links to the current page.
    /// </summary>
    public static PathString VisiblePath(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Items.TryGetValue(OriginalPathKey, out var value) && value is PathString original ? original : context.Request.Path;
    }

    /// <summary>
    /// <paramref name="pathWithAddress"/> (<c>/agence-1/fr/reserver?…</c>, written for the platform host) as a link of the
    /// site of this request: the address segment is dropped on the agency's own host (<c>/fr/reserver?…</c>), the path
    /// is kept as is anywhere else.
    /// </summary>
    public static string ForLinks(HttpContext context, string pathWithAddress)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(pathWithAddress);
        if (OwnHostOf(context) is null || !context.Items.TryGetValue(OwnHostSlugKey, out var value) || value is not string slug)
        {
            return pathWithAddress;
        }

        var root = "/" + slug;
        if (string.Equals(pathWithAddress, root, StringComparison.OrdinalIgnoreCase))
        {
            return "/";
        }

        return pathWithAddress.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase) || pathWithAddress.StartsWith(root + "?", StringComparison.OrdinalIgnoreCase)
            ? pathWithAddress[root.Length..]
            : pathWithAddress;
    }

    /// <summary>
    /// The WhatsApp number of the site's button and links: the platform number (<c>PublicSite:WhatsAppNumber</c>, the
    /// partner accounts are the platform's, ADR-0067), only on the site of the agency that number serves — an active
    /// agency whose WhatsApp module is on (<see cref="IWhatsAppNumber"/>); <see langword="null"/> elsewhere.
    /// </summary>
    public static string? WhatsAppNumber(HttpContext context, PublicSiteOptions options)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(options);
        return context.Items.TryGetValue(WhatsAppKey, out var served) && served is true && !string.IsNullOrWhiteSpace(options.WhatsAppNumber)
            ? options.WhatsAppNumber
            : null;
    }

    internal static void Set(HttpContext context, AgencySummary agency, bool servedByWhatsApp)
    {
        context.Items[ItemKey] = agency;
        context.Items[WhatsAppKey] = servedByWhatsApp;
    }

    /// <summary>Records that the request came on <paramref name="host"/>, an own host of the site at <paramref name="slug"/>, for <paramref name="originalPath"/>.</summary>
    internal static void SetOwnHost(HttpContext context, string host, string slug, PathString originalPath)
    {
        context.Items[OwnHostKey] = host;
        context.Items[OwnHostSlugKey] = slug;
        context.Items[OriginalPathKey] = originalPath;
    }
}

/// <summary>
/// Resolves the agency of a public address before anything reads the actor of the request: the route value
/// <see cref="PublicSiteAgency.RouteParameter"/> through the cached <see cref="IAgencyDirectory"/>. An unknown address, a
/// suspended agency or a site whose module is off answer 404 (the status code page then renders, as for any 404).
/// Whether the platform's WhatsApp number serves the agency is resolved at the same time (button and links of the site).
/// </summary>
internal sealed class PublicSiteAgencyMiddleware(RequestDelegate next, IAgencyDirectory agencies, IWhatsAppNumber whatsApp)
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

            PublicSiteAgency.Set(context, agency, await whatsApp.ServesAsync(agency.Id, context.RequestAborted));
        }

        await next(context);
    }
}

/// <summary>
/// Serves the site of an agency on the agency's own host names (ADR-0069). When the host of the request is one of them,
/// the path is rewritten from <c>/fr/…</c> to <c>/{agency}/fr/…</c> before the culture and the routes read it, so that
/// every public page, form and API works unchanged; the visitor never sees the address segment (the links of the site
/// leave it out, <see cref="PublicSiteAgency.SlugOf"/>). The addresses of the platform itself (back office, accounts,
/// probes, webhooks, payment returns, media, static files…) are never rewritten. A host that is not the primary one of
/// the agency redirects (301) to the primary host, same path and query. A suspended agency or a closed site answers as
/// on the platform host: 404. Placed before the request localization and the routing.
/// </summary>
internal sealed class AgencyHostMiddleware(RequestDelegate next, IAgencyDirectory agencies)
{
    /// <summary>Segments of the platform's APIs under <c>/api</c>; every other <c>/api/…</c> address is a site's (<c>/api/public</c>, <c>/api/v1</c>).</summary>
    private static readonly string[] PlatformApiSegments = ["channels", "openapi"];

    /// <summary>Static roots served from <c>wwwroot</c> that are not reserved words.</summary>
    private static readonly string[] StaticRoots = ["css", "js"];

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var host = context.Request.Host.Host;
        if (string.IsNullOrEmpty(host) || await agencies.FindByHostAsync(host, context.RequestAborted) is not { } agency)
        {
            await next(context);
            return;
        }

        var path = context.Request.Path;
        if (!IsSiteAddress(path))
        {
            await next(context);
            return;
        }

        if (agency.HasOpenPublicSite && agency.PrimaryHost is { } primary && !string.Equals(primary, host.ToLowerInvariant(), StringComparison.Ordinal))
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Redirect($"{context.Request.Scheme}://{primary}{path}{context.Request.QueryString}", permanent: true);
            return;
        }

        PublicSiteAgency.SetOwnHost(context, host.ToLowerInvariant(), agency.Slug, path);
        context.Request.Path = "/" + agency.Slug + (path.HasValue && path.Value != "/" ? path.Value : string.Empty);
        await next(context);
    }

    /// <summary>
    /// Whether <paramref name="path"/> on an agency's own host is an address of its site (to rewrite): the root, or a first
    /// segment that is neither a word of the platform (<c>/admin</c>, <c>/account</c>, <c>/health</c>…, except the APIs of a
    /// site under <c>/api</c>), nor a static file or folder (<c>/_framework</c>, <c>/css</c>, <c>/favicon.svg</c>, <c>/robots.txt</c>).
    /// </summary>
    internal static bool IsSiteAddress(PathString path)
    {
        var segments = (path.Value ?? string.Empty).Split('/', 3, StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return true;
        }

        var first = segments[0].ToLowerInvariant();
        if (first.StartsWith('_') || first.Contains('.', StringComparison.Ordinal) || StaticRoots.Contains(first, StringComparer.Ordinal))
        {
            return false;
        }

        if (first == "api")
        {
            return segments.Length < 2 || !PlatformApiSegments.Contains(segments[1].ToLowerInvariant(), StringComparer.Ordinal);
        }

        return !AgencySlug.Reserved.Contains(first);
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
        return Results.Redirect(PublicRoutes.PathFor(PublicSiteAgency.SlugOf(context), PublicPage.Home, culture));
    }
}
