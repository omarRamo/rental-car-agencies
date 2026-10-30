namespace CarRental.Web.Components.Public.Site;

/// <summary>
/// Absolute addresses of the public site: canonical links, <c>hreflang</c> alternates, Open Graph and the sitemap use
/// <c>PublicSite:BaseUrl</c> when configured (never the Host header, which a client controls), else the request origin.
/// On the own host of an agency's site (ADR-0069) the base is that host, checked against the directory of the agencies.
/// </summary>
public static class PublicUrls
{
    /// <summary>The base address with a trailing slash.</summary>
    public static Uri BaseUri(Uri? configured, HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (PublicSiteAgency.OwnHostOf(request.HttpContext) is { } ownHost)
        {
            return new Uri($"{request.Scheme}://{ownHost}/");
        }

        if (configured is { IsAbsoluteUri: true })
        {
            return configured.AbsoluteUri.EndsWith('/') ? configured : new Uri(configured.AbsoluteUri + "/");
        }

        return new Uri($"{request.Scheme}://{request.Host}{request.PathBase}/");
    }

    /// <summary>The base address of a site served on its own host <paramref name="host"/> (<c>https://www.agence.tn/</c>), with the scheme of the request.</summary>
    public static Uri OwnHostBaseUri(string host, HttpRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentNullException.ThrowIfNull(request);
        return new Uri($"{request.Scheme}://{host}/");
    }

    /// <summary>Absolute address of an app-relative path (<c>/agence-1/fr/agence</c>).</summary>
    public static Uri Absolute(Uri baseUri, string path)
    {
        ArgumentNullException.ThrowIfNull(baseUri);
        ArgumentNullException.ThrowIfNull(path);
        return new Uri(baseUri, path.TrimStart('/'));
    }

    /// <summary>
    /// The <c>hreflang</c> alternates of a page of the site of <paramref name="agency"/>: one per enabled culture, plus
    /// <c>x-default</c> — the address of the site (<c>/agence-1</c>, or the root of its own host, which redirects to the
    /// visitor's language) for the home page, the default culture elsewhere.
    /// </summary>
    public static IReadOnlyList<(string HrefLang, Uri Url)> Alternates(string agency, PublicPage page, Uri baseUri, IReadOnlyList<string> cultures, string defaultCulture)
    {
        ArgumentNullException.ThrowIfNull(agency);
        ArgumentNullException.ThrowIfNull(cultures);
        var alternates = cultures.Select(culture => (culture, Absolute(baseUri, PublicRoutes.PathFor(agency, page, culture)))).ToList();
        var siteRoot = agency.Length == 0 ? "/" : PublicRoutes.Root(agency);
        alternates.Add(("x-default", page == PublicPage.Home ? Absolute(baseUri, siteRoot) : Absolute(baseUri, PublicRoutes.PathFor(agency, page, defaultCulture))));
        return alternates;
    }
}
