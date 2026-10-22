using CarRental.Application.Referential.Contracts;
using CarRental.Web.Components.Public.Site;
using CarRental.Web.Localization;
using Microsoft.AspNetCore.Components;

namespace CarRental.Web.Components.Public;

/// <summary>
/// Base of the public pages: the agency of the site and the culture of the route (ADR-0067, ADR-0015), and the
/// redirection of a non-canonical address (another culture's slug such as <c>/agence-1/en/reserver</c>, an upper-case
/// agency or culture) to the canonical one, query kept.
/// </summary>
public abstract class PublicPageBase : ComponentBase
{
    /// <summary>The address of the agency's site as typed in the URL (resolved by the public site middleware).</summary>
    [Parameter]
    public string Agency { get; set; } = string.Empty;

    /// <summary>Culture prefix of the URL (validated by the culture middleware).</summary>
    [Parameter]
    public string Culture { get; set; } = CultureCatalog.Default;

    [CascadingParameter]
    protected HttpContext HttpContext { get; set; } = default!;

    [Inject]
    protected NavigationManager Navigation { get; set; } = default!;

    /// <summary>The agency whose site this page belongs to (active, public site module on).</summary>
    protected AgencySummary SiteAgency => PublicSiteAgency.Require(HttpContext);

    /// <summary>The canonical address of the agency's site, for the links of the page (<c>agence-1</c>).</summary>
    protected string Slug => SiteAgency.Slug;

    /// <summary>Lower-case culture of the page, for its links.</summary>
    protected string Lang => Culture.ToLowerInvariant();

    /// <summary>Request cancellation (client gone).</summary>
    protected CancellationToken Aborted => HttpContext.RequestAborted;

    /// <summary>
    /// <see langword="true"/> when the request address is the canonical address of <paramref name="page"/>; otherwise
    /// redirects to it and returns <see langword="false"/> (the page then renders nothing).
    /// </summary>
    protected bool EnsureCanonical(PublicPage page)
    {
        var segments = (HttpContext.Request.Path.Value ?? string.Empty).Split('/', StringSplitOptions.RemoveEmptyEntries);
        var slug = segments.Length > 2 ? segments[2] : string.Empty;
        if (string.Equals(Agency, Slug, StringComparison.Ordinal)
            && string.Equals(Culture, Lang, StringComparison.Ordinal)
            && PublicRoutes.IsCanonical(page, Lang, slug))
        {
            return true;
        }

        Navigation.NavigateTo(PublicRoutes.PathFor(Slug, page, Lang) + HttpContext.Request.QueryString.Value);
        return false;
    }
}
