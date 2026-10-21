using System.Text;
using System.Xml;
using CarRental.Web.Components.Public;
using CarRental.Web.Components.Public.Site;
using CarRental.Web.Hosting;
using CarRental.Web.Localization;
using Microsoft.Extensions.Options;

namespace CarRental.Web.Endpoints.PublicSite;

/// <summary>
/// <c>/sitemap.xml</c> (indexable public pages × enabled cultures, with their <c>hreflang</c> alternates) and
/// <c>/robots.txt</c> (back office, account pages, APIs, booking funnel and "Ma réservation" excluded). Outside
/// production — or with <c>PublicSite:AllowIndexing=false</c> — robots.txt keeps the whole site out of search engines.
/// </summary>
public static class SeoEndpoints
{
    public const string CacheControl = "public, max-age=3600";

    private const string SitemapNamespace = "http://www.sitemaps.org/schemas/sitemap/0.9";
    private const string XhtmlNamespace = "http://www.w3.org/1999/xhtml";

    /// <summary>Paths kept out of search engines in every environment, before the per-culture funnel paths.</summary>
    private static readonly string[] PrivatePaths = ["/admin", "/account", "/api/", "/culture/", "/status/", "/health", "/media/vehicles/"];

    public static IEndpointRouteBuilder MapSeoEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapGet("/sitemap.xml", (HttpContext context, IOptions<PublicSiteOptions> site, CultureCatalog cultures) =>
            {
                context.Response.Headers.CacheControl = CacheControl;
                var baseUri = PublicUrls.BaseUri(site.Value.BaseUrl, context.Request);
                return Results.Text(Sitemap(baseUri, cultures.Enabled), "application/xml; charset=utf-8", Encoding.UTF8);
            })
            .AllowAnonymous()
            .ExcludeFromDescription();

        endpoints.MapGet("/robots.txt", (HttpContext context, IOptions<PublicSiteOptions> site, CultureCatalog cultures, IHostEnvironment environment) =>
            {
                context.Response.Headers.CacheControl = CacheControl;
                var baseUri = PublicUrls.BaseUri(site.Value.BaseUrl, context.Request);
                var allowIndexing = site.Value.AllowIndexing ?? environment.IsProduction();
                return Results.Text(Robots(baseUri, CultureCatalog.Prepared, allowIndexing), "text/plain; charset=utf-8", Encoding.UTF8);
            })
            .AllowAnonymous()
            .ExcludeFromDescription();

        return endpoints;
    }

    /// <summary>The sitemap of the indexable pages in <paramref name="cultures"/>, with absolute addresses.</summary>
    public static string Sitemap(Uri baseUri, IReadOnlyList<string> cultures)
    {
        ArgumentNullException.ThrowIfNull(baseUri);
        ArgumentNullException.ThrowIfNull(cultures);
        var builder = new StringBuilder();
        var settings = new XmlWriterSettings { Indent = true, Encoding = new UTF8Encoding(false), OmitXmlDeclaration = false };
        using (var writer = XmlWriter.Create(new StringWriterWithEncoding(builder), settings))
        {
            writer.WriteStartDocument();
            writer.WriteStartElement("urlset", SitemapNamespace);
            writer.WriteAttributeString("xmlns", "xhtml", null, XhtmlNamespace);
            foreach (var page in PublicRoutes.Indexable)
            {
                var alternates = PublicUrls.Alternates(page, baseUri, cultures, CultureCatalog.Default);
                foreach (var culture in cultures)
                {
                    writer.WriteStartElement("url", SitemapNamespace);
                    writer.WriteElementString("loc", SitemapNamespace, PublicUrls.Absolute(baseUri, PublicRoutes.PathFor(page, culture)).AbsoluteUri);
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
    /// robots.txt: private areas, then the booking funnel and "Ma réservation" in every prepared culture, and the
    /// sitemap; or everything disallowed when indexing is off.
    /// </summary>
    public static string Robots(Uri baseUri, IReadOnlyList<string> cultures, bool allowIndexing)
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

        foreach (var path in PrivatePaths.Concat(cultures.SelectMany(PublicRoutes.PrivatePrefixes)))
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
