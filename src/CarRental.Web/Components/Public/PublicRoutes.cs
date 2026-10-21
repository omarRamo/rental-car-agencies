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
/// Localized addresses of the public site (docs/conception/phase-2-vitrine-paiement.md §1): <c>/fr/reserver</c> and
/// <c>/en/book</c> are the same page. Every page declares the slug of each culture as a route; a slug of another culture
/// (<c>/en/reserver</c>) redirects to the canonical one. Arabic, prepared but disabled, uses the French slugs for now.
/// </summary>
public static class PublicRoutes
{
    /// <summary>Target of the "Choose" buttons of the results page (POST, antiforgery). Package 2C implements it.</summary>
    public const string SelectionRouteTemplate = "/{culture:length(2)}/reserver/selection";

    private static readonly Dictionary<PublicPage, (string French, string English)> Slugs = new()
    {
        [PublicPage.Home] = (string.Empty, string.Empty),
        [PublicPage.Results] = ("reserver", "book"),
        [PublicPage.Agency] = ("agence", "agency"),
        [PublicPage.Faq] = ("faq", "faq"),
        [PublicPage.Terms] = ("conditions", "terms"),
        [PublicPage.Privacy] = ("confidentialite", "privacy"),
    };

    /// <summary>"Ma réservation" (package 2C), kept out of search engines by robots.txt.</summary>
    private static readonly (string French, string English) MyBookingSlugs = ("ma-reservation", "my-booking");

    /// <summary>Pages listed in the sitemap, in this order.</summary>
    public static IReadOnlyList<PublicPage> Indexable { get; } = [PublicPage.Home, PublicPage.Agency, PublicPage.Faq, PublicPage.Terms, PublicPage.Privacy];

    /// <summary>The slug of <paramref name="page"/> in <paramref name="culture"/> (empty for the home page).</summary>
    public static string Slug(PublicPage page, string culture)
    {
        var (french, english) = Slugs[page];
        return IsEnglish(culture) ? english : french;
    }

    /// <summary>App-relative path of a page, e.g. <c>/en/book</c>.</summary>
    public static string PathFor(PublicPage page, string culture)
    {
        var normalized = Normalize(culture);
        var slug = Slug(page, normalized);
        return slug.Length == 0 ? $"/{normalized}" : $"/{normalized}/{slug}";
    }

    /// <summary>
    /// A link to an element of the current page (<c>/fr/reserver?…#main</c>). The pages declare <c>&lt;base href="/"&gt;</c>,
    /// against which a bare <c>#main</c> would resolve to the root and leave the page.
    /// </summary>
    public static string InPage(Microsoft.AspNetCore.Components.NavigationManager navigation, string elementId)
    {
        ArgumentNullException.ThrowIfNull(navigation);
        var current = "/" + navigation.ToBaseRelativePath(navigation.Uri);
        var fragmentStart = current.IndexOf('#', StringComparison.Ordinal);
        return $"{(fragmentStart < 0 ? current : current[..fragmentStart])}#{elementId}";
    }

    /// <summary>Address of the "Choose" action for <paramref name="culture"/>.</summary>
    public static string SelectionPath(string culture) => $"/{Normalize(culture)}/reserver/selection";

    /// <summary>Path prefixes of the booking funnel and "Ma réservation", in every culture (robots.txt).</summary>
    public static IEnumerable<string> PrivatePrefixes(string culture)
    {
        var normalized = Normalize(culture);
        return new[] { Slugs[PublicPage.Results].French, Slugs[PublicPage.Results].English, MyBookingSlugs.French, MyBookingSlugs.English }
            .Distinct(StringComparer.Ordinal)
            .Select(slug => $"/{normalized}/{slug}");
    }

    /// <summary>The page at <paramref name="path"/> (<c>/en/agency</c>, any culture's slug), or <see langword="null"/>.</summary>
    public static PublicPage? PageOf(string? path)
    {
        var segments = (path ?? string.Empty).Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length is 0 or > 2 || !CultureCatalog.Prepared.Contains(segments[0], StringComparer.OrdinalIgnoreCase))
        {
            return null;
        }

        var slug = segments.Length == 2 ? segments[1] : string.Empty;
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
    /// The same address in <paramref name="targetCulture"/>: the culture prefix is replaced and a known first slug is
    /// translated (<c>/fr/reserver/abc?x=1</c> → <c>/en/book/abc?x=1</c>); the rest of the path and the query are kept.
    /// </summary>
    public static string Localize(string pathAndQuery, string targetCulture)
    {
        ArgumentNullException.ThrowIfNull(pathAndQuery);
        var target = Normalize(targetCulture);
        var queryStart = pathAndQuery.IndexOf('?', StringComparison.Ordinal);
        var path = queryStart < 0 ? pathAndQuery : pathAndQuery[..queryStart];
        var query = queryStart < 0 ? string.Empty : pathAndQuery[queryStart..];

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || !CultureCatalog.Prepared.Contains(segments[0], StringComparer.OrdinalIgnoreCase))
        {
            return $"/{target}{query}";
        }

        if (segments.Length > 1)
        {
            segments[1] = TranslateSlug(segments[1], target);
        }

        // Pages of the booking funnel and of "Ma réservation" (/fr/reserver/{token}/conducteurs → /en/book/{token}/drivers).
        for (var index = 2; index < segments.Length; index++)
        {
            segments[index] = TranslateStepSlug(segments[index], target);
        }

        segments[0] = target;
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
