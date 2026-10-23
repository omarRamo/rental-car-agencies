using System.Globalization;
using CarRental.Application.Common.Security;
using CarRental.Web.Components.Public.Site;
using CarRental.Web.Hosting;
using CarRental.Web.Security;
using Microsoft.AspNetCore.DataProtection;

namespace CarRental.Web.Endpoints.PublicSite;

/// <summary>
/// The actors of the public booking pages: their writes run in a DI scope of their own, as a system actor that keeps
/// the address of the visitor (audit), once the endpoint checked its own token or session (ADR-0049, ADR-0051). The
/// actor works for the agency of the public site only (ADR-0067): a session, a link or a booking of another agency is
/// unknown to it, and it writes nothing for another agency.
/// </summary>
public static class PublicActors
{
    public const string Funnel = "system:public-funnel";

    public const string MyBooking = "system:my-booking";

    /// <summary>
    /// A scope acting as <paramref name="actor"/> for the agency of the public site of the request, with the client
    /// address and the correlation of the request. Outside a public site (no agency), the actor is a visitor of no
    /// agency: it sees and writes nothing.
    /// </summary>
    public static AsyncServiceScope CreateScope(HttpContext context, string actor)
    {
        ArgumentNullException.ThrowIfNull(context);
        var scope = context.RequestServices.GetRequiredService<IServiceScopeFactory>().CreateAsyncScope();
        var ip = context.RequestServices.GetRequiredService<ClientIpResolver>().Resolve(context);
        var correlationId = CorrelationIds.Current(context);
        scope.ServiceProvider.GetRequiredService<ScopedUserContext>().Initialize(
            PublicSiteAgency.Of(context) is { } agencyId
                ? UserContextSnapshot.System(actor, correlationId, agencyId) with { IpAddress = ip }
                : UserContextSnapshot.Anonymous(ip, correlationId));
        return scope;
    }

    /// <summary>The address of the site the visitor uses (links of the e-mails when <c>PublicSite:BaseUrl</c> is not set; hosts are filtered).</summary>
    public static string BaseUrl(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return $"{context.Request.Scheme}://{context.Request.Host}{context.Request.PathBase}/";
    }
}

/// <summary>
/// The cookies of the public booking pages, protected by Data Protection (encrypted and signed, with an expiry):
/// <list type="bullet">
/// <item><see cref="RequestCookie"/>: the request just submitted from this browser, to show its confirmation and payment
/// (a booking number alone shows a generic page);</item>
/// <item><see cref="MyBookingCookie"/>: the session of "Ma réservation", limited to one booking, at most 24 hours, sliding.</item>
/// </list>
/// Both are HTTP only, SameSite Lax and secure over HTTPS (always outside development and tests), and limited to the
/// addresses of the site that set them (<c>Path=/{agency}</c>, ADR-0067): a browser never sends the session of one
/// agency's site to another's.
/// </summary>
public sealed class PublicBookingCookies(IDataProtectionProvider dataProtection, IHostEnvironment environment, TimeProvider clock)
{
    public const string RequestCookie = "carrental.request";

    public const string MyBookingCookie = "carrental.mybooking";

    /// <summary>The confirmation of a request stays readable a day on the browser that sent it.</summary>
    public static readonly TimeSpan RequestLifetime = TimeSpan.FromHours(24);

    private readonly ITimeLimitedDataProtector _requests = dataProtection.CreateProtector("CarRental.PublicBooking.Request.v1").ToTimeLimitedDataProtector();

    private readonly ITimeLimitedDataProtector _sessions = dataProtection.CreateProtector("CarRental.MyBooking.Session.v1").ToTimeLimitedDataProtector();

    public void SetRequest(HttpContext context, Guid bookingId)
    {
        ArgumentNullException.ThrowIfNull(context);
        var expires = clock.GetUtcNow() + RequestLifetime;
        context.Response.Cookies.Append(RequestCookie, _requests.Protect(bookingId.ToString("N"), expires), Options(context, expires));
    }

    /// <summary>The request of the cookie, when it is well formed, unexpired and not tampered with.</summary>
    public Guid? ReadRequest(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Unprotect(_requests, context.Request.Cookies[RequestCookie]) is { } value && Guid.TryParseExact(value, "N", out var id) ? id : null;
    }

    public void SetSession(HttpContext context, Guid grantId, Guid bookingId, DateTimeOffset expiresAtUtc)
    {
        ArgumentNullException.ThrowIfNull(context);
        var value = string.Create(CultureInfo.InvariantCulture, $"{grantId:N}.{bookingId:N}");
        context.Response.Cookies.Append(MyBookingCookie, _sessions.Protect(value, expiresAtUtc), Options(context, expiresAtUtc));
    }

    /// <summary>The grant and booking of the session cookie (checked against the server side by the caller).</summary>
    public (Guid GrantId, Guid BookingId)? ReadSession(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var value = Unprotect(_sessions, context.Request.Cookies[MyBookingCookie]);
        var parts = value?.Split('.');
        return parts is { Length: 2 } && Guid.TryParseExact(parts[0], "N", out var grant) && Guid.TryParseExact(parts[1], "N", out var booking)
            ? (grant, booking)
            : null;
    }

    public void ClearSession(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Response.Cookies.Delete(MyBookingCookie, Options(context, DateTimeOffset.UnixEpoch));
    }

    private static string? Unprotect(ITimeLimitedDataProtector protector, string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 1024)
        {
            return null;
        }

        try
        {
            return protector.Unprotect(value);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            // Expired, tampered with, or from another key ring: no session.
            return null;
        }
    }

    private CookieOptions Options(HttpContext context, DateTimeOffset expires) => new()
    {
        HttpOnly = true,
        SameSite = SameSiteMode.Lax,
        Secure = context.Request.IsHttps || !(environment.IsDevelopment() || environment.IsEnvironment(WebEnvironments.Testing)),
        Path = PublicSiteAgency.SiteOf(context) is { } site ? $"/{site.Slug}" : "/",
        Expires = expires,
        IsEssential = true,
    };
}
