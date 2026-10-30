using System.Text;
using System.Xml;
using CarRental.Application.Referential.Contracts;
using CarRental.Web.Components.Public;
using CarRental.Web.Components.Public.Site;
using CarRental.Web.Hosting;
using CarRental.Web.Localization;
using Microsoft.Extensions.Options;

namespace CarRental.Web.Endpoints.PublicSite;

/// <summary>
/// <c>/sitemap.xml</c> (every open site of an agency × its indexable pages × the enabled cultures, with their
/// <c>hreflang</c> alternates, ADR-0067) and <c>/robots.txt</c> (back office, account pages, APIs, booking funnel and
/// "Ma réservation" of every site excluded). Outside production — or with <c>PublicSite:AllowIndexing=false</c> —
/// robots.txt keeps the whole platform out of search engines. Both stay at the root: they cover every site.
/// </summary>
public static class SeoEndpoints
{
    public const string CacheControl = "public, max-age=3600";

    private const string SitemapNamespace = "http://www.sitemaps.org/schemas/sitemap/0.9";
    private const string XhtmlNamespace = "http://www.w3.org/1999/xhtml";

    /// <summary>
    /// Paths kept out of search engines in every environment, before the per-culture funnel paths: the platform's own,
    /// and the APIs of every site (<c>/{agency}/api/…</c>).
    /// </summary>
    private static readonly string[] PrivatePaths = ["/admin", "/account", "/api/", "/*/api/", "/culture/", "/status/", "/health", "/media/vehicles/"];

    public static IEndpointRouteBuilder MapSeoEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapGet("/sitemap.xml", async (HttpContext context, IOptions<PublicSiteOptions> site, CultureCatalog cultures, IAgencyDirectory agencies) =>
            {
                context.Response.Headers.CacheControl = CacheControl;
                var platformBase = PublicUrls.BaseUri(site.Value.BaseUrl, context.Request);
                var open = (await agencies.ListAsync(context.RequestAborted)).Where(a => a.HasOpenPublicSite);

                // On the own host of an agency (ADR-0069), the sitemap of that site only; on the platform host, every
                // open site, each at its canonical address (its own host when it has one).
                var ownHost = context.Request.Host.Host.ToLowerInvariant();
                var sites = (await agencies.FindByHostAsync(ownHost, context.RequestAborted) is { } owner
                        ? open.Where(a => a.Id == owner.Id)
                        : open.OrderBy(a => a.Slug, StringComparer.Ordinal))
                    .Select(a => a.PrimaryHost is { } host
                        ? new SitemapSite(PublicUrls.OwnHostBaseUri(host, context.Request), string.Empty)
                        : new SitemapSite(platformBase, a.Slug))
                    .ToList();
                return Results.Text(Sitemap(sites, cultures.Enabled), "application/xml; charset=utf-8", Encoding.UTF8);
            })
            .AllowAnonymous()
            .ExcludeFromDescription();

        endpoints.MapGet("/robots.txt", async (HttpContext context, IOptions<PublicSiteOptions> site, CultureCatalog cultures, IHostEnvironment environment, IAgencyDirectory agencies) =>
            {
                context.Response.Headers.CacheControl = CacheControl;
                var ownHost = await agencies.FindByHostAsync(context.Request.Host.Host, context.RequestAborted) is not null;
                var baseUri = ownHost ? PublicUrls.OwnHostBaseUri(context.Request.Host.Host.ToLowerInvariant(), context.Request) : PublicUrls.BaseUri(site.Value.BaseUrl, context.Request);
                var allowIndexing = site.Value.AllowIndexing ?? environment.IsProduction();
                return Results.Text(Robots(baseUri, CultureCatalog.Prepared, allowIndexing, ownHost), "text/plain; charset=utf-8", Encoding.UTF8);
            })
            .AllowAnonymous()
            .ExcludeFromDescription();

        return endpoints;
    }

    /// <summary>A site of the sitemap: its base address and the first segment of its links (<c>agence-1</c>, or nothing on its own host).</summary>
    public sealed record SitemapSite(Uri BaseUri, string Agency);

    /// <summary>The sitemap of the indexable pages of the sites of the platform host at <paramref name="baseUri"/>, in <paramref name="cultures"/>.</summary>
    public static string Sitemap(Uri baseUri, IReadOnlyList<string> sites, IReadOnlyList<string> cultures)
    {
        ArgumentNullException.ThrowIfNull(sites);
        return Sitemap([.. sites.Select(agency => new SitemapSite(baseUri, agency))], cultures);
    }

    /// <summary>The sitemap of the indexable pages of <paramref name="sites"/> in <paramref name="cultures"/>, with absolute addresses.</summary>
    public static string Sitemap(IReadOnlyList<SitemapSite> sites, IReadOnlyList<string> cultures)
    {
        ArgumentNullException.ThrowIfNull(sites);
        ArgumentNullException.ThrowIfNull(cultures);
        var builder = new StringBuilder();
        var settings = new XmlWriterSettings { Indent = true, Encoding = new UTF8Encoding(false), OmitXmlDeclaration = false };
        using (var writer = XmlWriter.Create(new StringWriterWithEncoding(builder), settings))
        {
            writer.WriteStartDocument();
            writer.WriteStartElement("urlset", SitemapNamespace);
            writer.WriteAttributeString("xmlns", "xhtml", null, XhtmlNamespace);
            foreach (var (site, page) in sites.SelectMany(site => PublicRoutes.Indexable.Select(page => (site, page))))
            {
                var (baseUri, agency) = (site.BaseUri, site.Agency);
                var alternates = PublicUrls.Alternates(agency, page, baseUri, cultures, CultureCatalog.Default);
                foreach (var culture in cultures)
                {
                    writer.WriteStartElement("url", SitemapNamespace);
                    writer.WriteElementString("loc", SitemapNamespace, PublicUrls.Absolute(baseUri, PublicRoutes.PathFor(agency, page, culture)).AbsoluteUri);
                    foreach (var (hrefLang, url) in alternates)
                    {
                        writer.WriteStartElement("xhtml", "link", XhtmlNamespace);
                        writer.WriteAttributeString("rel", "alternate");
                        writer.WriteAttributeString("hreflang", hrefLang);
                        writer.WriteAttributeString("href", url.AbsoluteUri);
                        writer.WriteEndElement();
                    }

                    writer.WriteEndElement();
                }
            }

            writer.WriteEndElement();
            writer.WriteEndDocument();
        }

        return builder.ToString();
    }

    /// <summary>
    /// robots.txt: private areas, then the booking funnel and "Ma réservation" of every site in every prepared culture
    /// (<c>/*/fr/reserver</c>: the first segment is the site), and the sitemap; or everything disallowed when indexing is off.
    /// </summary>
    public static string Robots(Uri baseUri, IReadOnlyList<string> cultures, bool allowIndexing, bool ownHost = false)
    {
        ArgumentNullException.ThrowIfNull(baseUri);
        ArgumentNullException.ThrowIfNull(cultures);
        var builder = new StringBuilder();
        builder.Append("User-agent: *\n");
        if (!allowIndexing)
        {
            builder.Append("Disallow: /\n");
            return builder.ToString();
        }

        foreach (var path in PrivatePaths.Concat(cultures.SelectMany(culture => PublicRoutes.PrivatePrefixes(culture, ownHost))))
        {
            builder.Append("Disallow: ").Append(path).Append('\n');
        }

        builder.Append("Allow: /\n\n");
        builder.Append("Sitemap: ").Append(new Uri(baseUri, "sitemap.xml").AbsoluteUri).Append('\n');
        return builder.ToString();
    }

    /// <summary>A string writer that declares UTF-8 (the XML declaration takes the encoding of its writer).</summary>
    private sealed class StringWriterWithEncoding(StringBuilder builder) : StringWriter(builder, System.Globalization.CultureInfo.InvariantCulture)
    {
        public override Encoding Encoding => Encoding.UTF8;
    }
}
