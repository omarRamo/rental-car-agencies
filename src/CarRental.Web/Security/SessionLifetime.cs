using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Options;

namespace CarRental.Web.Security;

/// <summary>Staff sessions (<c>Identity:Session</c>).</summary>
public sealed class StaffSessionOptions
{
    public const string SectionName = "Identity:Session";

    /// <summary>
    /// A session ends this long after its sign-in, however active it is (12 hours: ASVS 3.3.2, level 2): the user signs in
    /// again with the password and the second factor. The 8-hour sliding expiry of the cookie still ends an idle session.
    /// </summary>
    public TimeSpan AbsoluteLifetime { get; set; } = TimeSpan.FromHours(12);

    internal bool IsValid => AbsoluteLifetime > TimeSpan.Zero && AbsoluteLifetime <= TimeSpan.FromDays(7);
}

/// <summary>
/// Absolute lifetime of the staff sessions (ADR-0062). The sign-in instant is a claim of the session
/// (<see cref="SignInTimeClaimType"/>, Unix seconds as in OpenID Connect) set when the cookie is first issued, kept when
/// the security stamp validation rebuilds the claims (<see cref="SessionClaims"/>), and checked on every HTTP request
/// (cookie validation) and every two minutes in the interactive circuits — which otherwise outlive the cookie.
/// </summary>
internal static partial class SessionLifetime
{
    public const string SignInTimeClaimType = "auth_time";

    /// <summary>The sign-in instant of the session, or <see langword="null"/> for a session issued before this claim existed.</summary>
    public static DateTimeOffset? SignedInAt(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        return long.TryParse(principal.FindFirstValue(SignInTimeClaimType), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : null;
    }

    /// <summary>True once <paramref name="lifetime"/> has passed since the sign-in (a session without sign-in instant is stamped first).</summary>
    public static bool HasExpired(ClaimsPrincipal principal, DateTimeOffset now, TimeSpan lifetime) =>
        SignedInAt(principal) is { } signedIn && now - signedIn >= lifetime;

    /// <summary>Records <paramref name="now"/> as the sign-in instant, unless the principal already carries one.</summary>
    public static bool Stamp(ClaimsPrincipal principal, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(principal);
        if (SignedInAt(principal) is not null || principal.Identities.FirstOrDefault(i => i.IsAuthenticated) is not { } identity)
        {
            return false;
        }

        identity.AddClaim(new Claim(SignInTimeClaimType, now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture), ClaimValueTypes.Integer64));
        return true;
    }

    /// <summary>Wires the lifetime into the events of the application cookie (after the security stamp validation of Identity).</summary>
    public static void Configure(CookieAuthenticationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var validateStamp = options.Events.OnValidatePrincipal;
        var signingIn = options.Events.OnSigningIn;
        options.Events.OnSigningIn = context =>
        {
            if (context.Principal is { } principal)
            {
                Stamp(principal, Now(context.HttpContext));
            }

            return signingIn(context);
        };
        options.Events.OnValidatePrincipal = context => ValidateAsync(context, validateStamp);
    }

    /// <summary>Ends an expired session; stamps an old session that has no sign-in instant yet; then validates the security stamp.</summary>
    internal static async Task ValidateAsync(CookieValidatePrincipalContext context, Func<CookieValidatePrincipalContext, Task> next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        if (context.Principal is { } principal)
        {
            var now = Now(context.HttpContext);
            var lifetime = context.HttpContext.RequestServices.GetRequiredService<IOptions<StaffSessionOptions>>().Value.AbsoluteLifetime;
            if (HasExpired(principal, now, lifetime))
            {
                LogExpired(context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(SessionLifetime).FullName!), principal.GetUserId());
                context.RejectPrincipal();
                await context.HttpContext.SignOutAsync(context.Scheme.Name);
                return;
            }

            if (Stamp(principal, now))
            {
                context.ShouldRenew = true;
            }
        }

        await next(context);
    }

    private static DateTimeOffset Now(HttpContext context) => context.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow();

    [LoggerMessage(Level = LogLevel.Information, Message = "Session of user {UserId} ended: its absolute lifetime is over (sign in again).")]
    private static partial void LogExpired(ILogger logger, Guid? userId);
}
