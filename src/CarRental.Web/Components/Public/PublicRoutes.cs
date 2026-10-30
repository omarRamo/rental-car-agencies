using CarRental.Web.Components.Public.Site;
using CarRental.Web.Localization;

namespace CarRental.Web.Components.Public;

/// <summary>Pages of the public site that have a localized address.</summary>
public enum PublicPage
{
    Home,
    Results,
    Agency,
    Faq,
    Terms,
    Privacy,
}

/// <summary>
/// Localized addresses of the public site (docs/conception/phase-2-vitrine-paiement.md §1, ADR-0067): every address starts
/// with the address of the agency's site, then the culture — <c>/agence-1/fr/reserver</c> and <c>/agence-1/en/book</c> are
/// the same page. Every page declares the slug of each culture as a route; a slug of another culture
/// (<c>/agence-1/en/reserver</c>) redirects to the canonical one. Arabic, prepared but disabled, uses the French slugs for now.
/// </summary>
public static class PublicRoutes
{
    /// <summary>The start of every public route (<see cref="PublicSiteAgency.RoutePrefix"/>).</summary>
    public const string RoutePrefix = PublicSiteAgency.RoutePrefix;

    /// <summary>Target of the "Choose" buttons of the results page (POST, antiforgery).</summary>
    public const string SelectionRouteTemplate = RoutePrefix + "/reserver/selection";

    private static readonly Dictionary<PublicPage, (string French, string English)> Slugs = new()
    {
        [PublicPage.Home] = (string.Empty, string.Empty),
        [PublicPage.Results] = ("reserver", "book"),
        [PublicPage.Agency] = ("agence", "agency"),
        [PublicPage.Faq] = ("faq", "faq"),
        [PublicPage.Terms] = ("conditions", "terms"),
        [PublicPage.Privacy] = ("confidentialite", "privacy"),
    };

    /// <summary>"Ma réservation", kept out of search engines by robots.txt.</summary>
    private static readonly (string French, string English) MyBookingSlugs = ("ma-reservation", "my-booking");

    /// <summary>Pages listed in the sitemap, in this order.</summary>
    public static IReadOnlyList<PublicPage> Indexable { get; } = [PublicPage.Home, PublicPage.Agency, PublicPage.Faq, PublicPage.Terms, PublicPage.Privacy];

    /// <summary>The slug of <paramref name="page"/> in <paramref name="culture"/> (empty for the home page).</summary>
    public static string Slug(PublicPage page, string culture)
    {
        var (french, english) = Slugs[page];
        return IsEnglish(culture) ? english : french;
    }

    /// <summary>
    /// The start of every address of the site of <paramref name="agency"/>: <c>/agence-1</c> on the platform host, nothing
    /// (<see cref="string.Empty"/>) for a site served on its own host, written with an empty <paramref name="agency"/>
    /// (ADR-0069, <see cref="Site.PublicSiteAgency.SlugOf"/>).
    /// </summary>
    public static string Root(string agency)
    {
        ArgumentNullException.ThrowIfNull(agency);
        if (agency.Length == 0)
        {
            return string.Empty;
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(agency);
        return "/" + agency;
    }

    /// <summary>App-relative path of a page of the site of <paramref name="agency"/>, e.g. <c>/agence-1/en/book</c> (<c>/en/book</c> on its own host).</summary>
    public static string PathFor(string agency, PublicPage page, string culture)
    {
        var root = Root(agency);
        var normalized = Normalize(culture);
        var slug = Slug(page, normalized);
        return slug.Length == 0 ? $"{root}/{normalized}" : $"{root}/{normalized}/{slug}";
    }

    /// <summary>
    /// A link to an element of the current page (<c>/agence-1/fr/reserver?…#main</c>, <c>/fr/reserver?…#main</c> on the
    /// agency's own host). The pages declare <c>&lt;base href="/"&gt;</c>, against which a bare <c>#main</c> would resolve
    /// to the root and leave the page.
    /// </summary>
    public static string InPage(HttpContext context, string elementId)
    {
        ArgumentNullException.ThrowIfNull(context);
        return $"{Site.PublicSiteAgency.VisiblePath(context)}{context.Request.QueryString}#{elementId}";
    }

    /// <summary>Address of the "Choose" action of the site of <paramref name="agency"/> in <paramref name="culture"/> (same slug in every language).</summary>
    public static string SelectionPath(string agency, string culture) => $"{Root(agency)}/{Normalize(culture)}/reserver/selection";

    /// <summary>
    /// Patterns of the booking funnel and of "Ma réservation" in <paramref name="culture"/>, for robots.txt: of every site
    /// of the platform host (<c>/*/fr/reserver</c>: the first segment is the site, whichever it is), or of the one site
    /// served on its own host (<c>/fr/reserver</c>, <paramref name="ownHost"/>).
    /// </summary>
    public static IEnumerable<string> PrivatePrefixes(string culture, bool ownHost = false)
    {
        var normalized = Normalize(culture);
        var root = ownHost ? string.Empty : "/*";
        return new[] { Slugs[PublicPage.Results].French, Slugs[PublicPage.Results].English, MyBookingSlugs.French, MyBookingSlugs.English }
            .Distinct(StringComparer.Ordinal)
            .Select(slug => $"{root}/{normalized}/{slug}");
    }

    /// <summary>
    /// The page at <paramref name="path"/> (<c>/agence-1/en/agency</c>, any culture's slug), or <see langword="null"/>
    /// when the path is not a page of a site.
    /// </summary>
    public static PublicPage? PageOf(string? path)
    {
        var segments = (path ?? string.Empty).Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length is < 2 or > 3 || !CultureCatalog.Prepared.Contains(segments[1], StringComparer.OrdinalIgnoreCase))
        {
            return null;
        }

        var slug = segments.Length == 3 ? segments[2] : string.Empty;
        foreach (var (page, (french, english)) in Slugs)
        {
            if (string.Equals(slug, french, StringComparison.OrdinalIgnoreCase) || string.Equals(slug, english, StringComparison.OrdinalIgnoreCase))
            {
                return page;
            }
        }

        return null;
    }

    /// <summary>Whether <paramref name="slug"/> is the canonical slug of <paramref name="page"/> in <paramref name="culture"/>.</summary>
    public static bool IsCanonical(PublicPage page, string culture, string? slug) =>
        string.Equals(Slug(page, culture), slug ?? string.Empty, StringComparison.Ordinal);

    /// <summary>
    /// The same address in <paramref name="targetCulture"/>: the agency is kept, the culture replaced and a known slug
    /// translated (<c>/agence-1/fr/reserver/abc?x=1</c> → <c>/agence-1/en/book/abc?x=1</c>); the rest of the path and the
    /// query are kept. An address without a culture leads to the home page of its site in that culture.
    /// </summary>
    public static string Localize(string pathAndQuery, string targetCulture)
    {
        ArgumentNullException.ThrowIfNull(pathAndQuery);
        var target = Normalize(targetCulture);
        var queryStart = pathAndQuery.IndexOf('?', StringComparison.Ordinal);
        var path = queryStart < 0 ? pathAndQuery : pathAndQuery[..queryStart];
        var query = queryStart < 0 ? string.Empty : pathAndQuery[queryStart..];

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return "/" + query;
        }

        if (segments.Length == 1 || !CultureCatalog.Prepared.Contains(segments[1], StringComparer.OrdinalIgnoreCase))
        {
            return $"/{segments[0]}/{target}{query}";
        }

        if (segments.Length > 2)
        {
            segments[2] = TranslateSlug(segments[2], target);
        }

        // Pages of the booking funnel and of "Ma réservation" (/{agency}/fr/reserver/{token}/conducteurs → …/en/book/{token}/drivers).
        for (var index = 3; index < segments.Length; index++)
        {
            segments[index] = TranslateStepSlug(segments[index], target);
        }

        segments[1] = target;
        return "/" + string.Join('/', segments) + query;
    }

    /// <summary>Slugs of the steps of the booking funnel and of the pages of "Ma réservation", in French and English.</summary>
    private static readonly (string French, string English)[] StepSlugs =
    [
        ("conducteurs", "drivers"),
        ("paiement", "payment"),
        ("redirection", "redirect"),
        ("dossier", "file"),
        ("acces", "access"),
        ("annulation", "cancellation"),
    ];

    /// <summary>The slug of a step of the booking funnel or of "Ma réservation" in <paramref name="culture"/> (<c>drivers</c>, <c>conducteurs</c>).</summary>
    public static string StepSlug(string frenchSlug, string culture)
    {
        foreach (var (french, english) in StepSlugs)
        {
            if (string.Equals(french, frenchSlug, StringComparison.Ordinal))
            {
                return IsEnglish(culture) ? english : french;
            }
        }

        return frenchSlug;
    }

    private static string TranslateStepSlug(string slug, string targetCulture)
    {
        foreach (var (french, english) in StepSlugs)
        {
            if (string.Equals(slug, french, StringComparison.OrdinalIgnoreCase) || string.Equals(slug, english, StringComparison.OrdinalIgnoreCase))
            {
                return IsEnglish(targetCulture) ? english : french;
            }
        }

        return slug;
    }

    private static string TranslateSlug(string slug, string targetCulture)
    {
        foreach (var (french, english) in Slugs.Values.Append(MyBookingSlugs))
        {
            if (string.Equals(slug, french, StringComparison.OrdinalIgnoreCase) || string.Equals(slug, english, StringComparison.OrdinalIgnoreCase))
            {
                return IsEnglish(targetCulture) ? english : french;
            }
        }

        return slug;
    }

    private static bool IsEnglish(string culture) => string.Equals(culture, "en", StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string culture) =>
        string.IsNullOrWhiteSpace(culture) ? CultureCatalog.Default : culture.Trim().ToLowerInvariant();
}
