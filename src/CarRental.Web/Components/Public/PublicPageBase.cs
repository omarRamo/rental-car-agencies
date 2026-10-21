using CarRental.Web.Localization;
using Microsoft.AspNetCore.Components;

namespace CarRental.Web.Components.Public;

/// <summary>
/// Base of the public pages: the culture of the route and the request, and the redirection of a non-canonical address
/// (another culture's slug such as <c>/en/reserver</c>, or an upper-case culture) to the canonical one, query kept.
/// </summary>
public abstract class PublicPageBase : ComponentBase
{
    /// <summary>Culture prefix of the URL (validated by the culture middleware).</summary>
    [Parameter]
    public string Culture { get; set; } = CultureCatalog.Default;

    [CascadingParameter]
    protected HttpContext HttpContext { get; set; } = default!;

    [Inject]
    protected NavigationManager Navigation { get; set; } = default!;

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
        var slug = segments.Length > 1 ? segments[1] : string.Empty;
        if (string.Equals(Culture, Lang, StringComparison.Ordinal) && PublicRoutes.IsCanonical(page, Lang, slug))
        {
            return true;
        }

        Navigation.NavigateTo(PublicRoutes.PathFor(page, Lang) + HttpContext.Request.QueryString.Value);
        return false;
    }
}
