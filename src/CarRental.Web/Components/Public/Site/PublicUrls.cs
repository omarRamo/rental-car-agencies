namespace CarRental.Web.Components.Public.Site;

/// <summary>
/// Absolute addresses of the public site: canonical links, <c>hreflang</c> alternates, Open Graph and the sitemap use
/// <c>PublicSite:BaseUrl</c> when configured (never the Host header, which a client controls), else the request origin.
/// </summary>
public static class PublicUrls
{
    /// <summary>The base address with a trailing slash.</summary>
    public static Uri BaseUri(Uri? configured, HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (configured is { IsAbsoluteUri: true })
        {
            return configured.AbsoluteUri.EndsWith('/') ? configured : new Uri(configured.AbsoluteUri + "/");
        }

        return new Uri($"{request.Scheme}://{request.Host}{request.PathBase}/");
    }

    /// <summary>Absolute address of an app-relative path (<c>/fr/agence</c>).</summary>
    public static Uri Absolute(Uri baseUri, string path)
    {
        ArgumentNullException.ThrowIfNull(baseUri);
        ArgumentNullException.ThrowIfNull(path);
        return new Uri(baseUri, path.TrimStart('/'));
    }

    /// <summary>
    /// The <c>hreflang</c> alternates of a page: one per enabled culture, plus <c>x-default</c> — the root (which
    /// redirects to the visitor's language) for the home page, the default culture elsewhere.
    /// </summary>
    public static IReadOnlyList<(string HrefLang, Uri Url)> Alternates(PublicPage page, Uri baseUri, IReadOnlyList<string> cultures, string defaultCulture)
    {
        ArgumentNullException.ThrowIfNull(cultures);
        var alternates = cultures.Select(culture => (culture, Absolute(baseUri, PublicRoutes.PathFor(page, culture)))).ToList();
        alternates.Add(("x-default", page == PublicPage.Home ? baseUri : Absolute(baseUri, PublicRoutes.PathFor(page, defaultCulture))));
        return alternates;
    }
}
