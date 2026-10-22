using CarRental.Application.Referential.Contracts;
using CarRental.Web.Hosting;
using CarRental.Web.Localization;
using Microsoft.Extensions.Options;

namespace CarRental.Web.Components.Public.Site;

/// <summary>
/// The agency whose public site a request shows (ADR-0067): the public pages, their forms and their APIs work for that
/// agency only, as a visitor, whether or not the browser carries a staff session. Resolved once per request by
/// <see cref="PublicSiteAgencyMiddleware"/> and read by the ambient user context.
/// </summary>
public static class PublicSiteAgency
{
    private const string ItemKey = "carrental.public-site-agency";

    /// <summary>The agency of the public site of this request, <see langword="null"/> outside the public site.</summary>
    public static Guid? Of(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Items.TryGetValue(ItemKey, out var value) && value is Guid agencyId ? agencyId : null;
    }

    internal static void Set(HttpContext context, Guid agencyId) => context.Items[ItemKey] = agencyId;

    /// <summary>
    /// True for an address of the public site: its pages (<c>/{culture}/…</c>), its APIs, the category photos, the return
    /// from the payment page and the simulated bank.
    /// </summary>
    /// <remarks>
    /// Transitional rule while the public site still serves one agency: the address of each agency's site
    /// (<c>/{slug}/{culture}/…</c>) replaces it.
    /// </remarks>
    public static bool IsPublicPath(PathString path, CultureCatalog cultures)
    {
        ArgumentNullException.ThrowIfNull(cultures);
        var value = path.Value;
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        // The vehicle photos (/media/vehicles) belong to the back office: they are served as the signed-in staff member.
        return cultures.IsEnabled(PathRequestCultureProvider.FirstSegment(value))
            || path.StartsWithSegments("/api/public", StringComparison.OrdinalIgnoreCase)
            || path.StartsWithSegments("/media/categories", StringComparison.OrdinalIgnoreCase)
            || path.StartsWithSegments("/payments", StringComparison.OrdinalIgnoreCase)
            || path.StartsWithSegments("/dev/clictopay", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The agency the public site presents: <c>PublicSite:AgencyId</c> when it is active, else the first active agency
    /// created (transitional, see <see cref="IsPublicPath"/>).
    /// </summary>
    public static async Task<Guid?> DefaultAsync(IAgencyDirectory agencies, PublicSiteOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(agencies);
        ArgumentNullException.ThrowIfNull(options);
        var active = (await agencies.ListAsync(cancellationToken)).Where(a => a.IsActive).ToList();
        return options.AgencyId is { } configured
            ? active.FirstOrDefault(a => a.Id == configured)?.Id
            : active.OrderBy(a => a.Id).FirstOrDefault()?.Id;
    }
}

/// <summary>Resolves the agency of a public site request before anything reads the actor of the request.</summary>
internal sealed class PublicSiteAgencyMiddleware(
    RequestDelegate next,
    IAgencyDirectory agencies,
    CultureCatalog cultures,
    IOptions<PublicSiteOptions> options)
{
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (PublicSiteAgency.IsPublicPath(context.Request.Path, cultures)
            && await PublicSiteAgency.DefaultAsync(agencies, options.Value, context.RequestAborted) is { } agencyId)
        {
            PublicSiteAgency.Set(context, agencyId);
        }

        await next(context);
    }
}
