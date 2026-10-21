using CarRental.Application.Bookings.Contracts;
using CarRental.Domain.Common;
using CarRental.Web.Components.Public.Search;
using CarRental.Web.Endpoints.PublicSite;

namespace CarRental.Web.Components.Public.Booking;

/// <summary>
/// Base of the pages of the booking funnel and of "Ma réservation" (ADR-0049, ADR-0051): never indexed nor cached,
/// rendered for one visitor (their address holds a token, or their content a session); the canonical address is the
/// slug of the culture of the page, the query being kept.
/// </summary>
public abstract class BookingPageBase : PublicPageBase
{
    /// <summary>
    /// <see langword="true"/> when the request address is <paramref name="canonicalPath"/>; otherwise redirects to it (the
    /// query kept) and returns <see langword="false"/>, the page then rendering nothing.
    /// </summary>
    protected bool EnsureCanonicalPath(string canonicalPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(canonicalPath);
        if (string.Equals(HttpContext.Request.Path.Value, canonicalPath, StringComparison.Ordinal))
        {
            return true;
        }

        Navigation.NavigateTo(canonicalPath + HttpContext.Request.QueryString.Value);
        return false;
    }

    /// <summary>The first value of a query parameter of the address.</summary>
    protected string? Query(string name)
    {
        var values = HttpContext.Request.Query[name];
        return values.Count > 0 && !string.IsNullOrWhiteSpace(values[0]) ? values[0]!.Trim() : null;
    }

    /// <summary>The error of the last form of the page (<c>erreur</c>), when it is one a visitor may see.</summary>
    protected string? LastError =>
        Query(BookingFunnelEndpoints.ErrorParameter) is { } code && (PublicForms.IsVisible(code) || code == PublicForms.GenericError) ? code : null;

    /// <summary>The text of an error code of a public form.</summary>
    protected string ErrorText(string code) => BookingFunnelEndpoints.ErrorText(HttpContext.RequestServices, code);

    /// <summary>The source of the visit as kept with the session or the request (measurement of the funnel).</summary>
    protected static TrafficSource TrafficOf(RequestAttributionInfo? attribution, SalesChannel channel) => attribution is { } source
        ? new TrafficSource(source.Source, source.UtmSource, source.UtmMedium, source.UtmCampaign, source.UtmTerm, source.UtmContent)
        : channel == SalesChannel.WhatsApp ? new TrafficSource(TrafficSource.WhatsAppSource) : TrafficSource.Direct;

    /// <summary>Leaves the page for <paramref name="path"/> (a session that cannot be shown, a step not reached yet).</summary>
    protected void Leave(string path) => Navigation.NavigateTo(path);
}
