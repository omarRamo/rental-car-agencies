using System.Globalization;
using CarRental.Application.Reporting.Contracts;
using CarRental.Web.Components.Public.Search;

namespace CarRental.Web.Endpoints.PublicSite;

/// <summary>
/// Records the funnel steps of a visit (ADR-0037): the anonymous visit cookie, the culture of the page and the traffic
/// source, given to <see cref="IFunnelEvents"/>. The results page records <c>search</c> and <c>results</c>; the booking
/// funnel (package 2C) records the following steps with the category and the booking. Robots are not measured, and a
/// visitor who signals an opt-out (Global Privacy Control, Do Not Track) gets no cookie and anonymous counts only.
/// Scoped: the steps recorded during one request share one visit and one cookie.
/// </summary>
public sealed class FunnelTracker(IFunnelEvents events)
{
    private Guid? _visitId;
    private bool _visitResolved;

    public Task TrackAsync(
        HttpContext context,
        FunnelStepInfo step,
        TrafficSource source,
        string? categoryCode = null,
        Guid? bookingId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(source);
        if (VisitCookie.IsLikelyRobot(context.Request.Headers.UserAgent.ToString()))
        {
            return Task.CompletedTask;
        }

        if (!_visitResolved)
        {
            _visitId = VisitCookie.HasOptedOut(context.Request) ? null : VisitCookie.Touch(context);
            _visitResolved = true;
        }

        var visitId = _visitId;
        return events.RecordAsync(
            new FunnelEventRecord(
                step,
                visitId,
                CultureInfo.CurrentUICulture.TwoLetterISOLanguageName,
                source.Channel,
                source.Source,
                source.UtmSource,
                source.UtmMedium,
                source.UtmCampaign,
                source.UtmTerm,
                source.UtmContent,
                categoryCode,
                bookingId),
            cancellationToken);
    }
}

/// <summary>
/// The anonymous visit identifier: a random value in a first-party cookie (<see cref="Name"/>, HTTP only, SameSite Lax,
/// secure over HTTPS), renewed at each measured step and forgotten after <see cref="Lifetime"/> without activity. It is
/// never linked to a person, a booking contact or an IP address.
/// </summary>
public static class VisitCookie
{
    public const string Name = "carrental.visit";

    /// <summary>A day without a measured step ends the visit (a locked quote of the funnel lasts 24 h as well).</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);

    private static readonly string[] RobotMarkers =
    [
        "bot", "crawl", "spider", "slurp", "lighthouse", "headlesschrome", "facebookexternalhit", "curl", "wget", "python-requests",
    ];

    /// <summary>The visit of the request, or a new one; the cookie is (re)written with a fresh expiry.</summary>
    public static Guid Touch(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var visitId = Read(context.Request) ?? Guid.NewGuid();
        if (!context.Response.HasStarted)
        {
            context.Response.Cookies.Append(
                Name,
                visitId.ToString("N", CultureInfo.InvariantCulture),
                new CookieOptions
                {
                    HttpOnly = true,
                    Secure = context.Request.IsHttps,
                    SameSite = SameSiteMode.Lax,
                    Path = "/",
                    MaxAge = Lifetime,
                    IsEssential = false,
                });
        }

        return visitId;
    }

    /// <summary>The visit carried by the request cookie, if well-formed.</summary>
    public static Guid? Read(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.Cookies.TryGetValue(Name, out var value)
               && value is { Length: 32 }
               && Guid.TryParseExact(value, "N", out var visitId)
               && visitId != Guid.Empty
            ? visitId
            : null;
    }

    /// <summary>Global Privacy Control (<c>Sec-GPC: 1</c>) or Do Not Track (<c>DNT: 1</c>).</summary>
    public static bool HasOptedOut(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.Headers["Sec-GPC"] == "1" || request.Headers["DNT"] == "1";
    }

    /// <summary>Crawlers, link previews (the WhatsApp preview fetcher, not its users), audit tools and scripts, by their user agent.</summary>
    public static bool IsLikelyRobot(string? userAgent) =>
        !string.IsNullOrWhiteSpace(userAgent)
        && (userAgent.StartsWith("WhatsApp/", StringComparison.OrdinalIgnoreCase)
            || RobotMarkers.Any(marker => userAgent.Contains(marker, StringComparison.OrdinalIgnoreCase)));
}
