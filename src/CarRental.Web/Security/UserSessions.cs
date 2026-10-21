using System.Collections.Concurrent;
using System.Security.Claims;
using CarRental.Application.Identity.Contracts;
using CarRental.Application.Identity.Users;
using CarRental.Infrastructure.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace CarRental.Web.Security;

/// <summary>
/// The users whose claims changed (extra permissions granted or revoked) while they may have open sessions. A cookie
/// issued before the change is revalidated at its next request by <see cref="CarRentalSecurityStampValidator"/>.
/// </summary>
/// <remarks>
/// In memory: the application runs on one App Service instance (ADR-0004, like the availability cache of ADR-0025). The
/// periodic security stamp validation (every minute) refreshes the claims of any session anyway, on any instance and
/// after a restart, so this registry only shortens the delay to the very next request (ADR-0029).
/// </remarks>
internal sealed class UserSessionRegistry(TimeProvider timeProvider) : IUserSessions
{
    /// <summary>Far longer than the security stamp validation interval: by then every session has been refreshed.</summary>
    private static readonly TimeSpan Retention = TimeSpan.FromHours(1);

    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _changes = new();

    public void RefreshClaims(Guid userId)
    {
        var now = timeProvider.GetUtcNow();
        _changes[userId] = now;
        foreach (var change in _changes.Where(c => now - c.Value > Retention))
        {
            _changes.TryRemove(change);
        }
    }

    /// <summary>
    /// True when the claims of <paramref name="userId"/> changed at or after <paramref name="issuedUtc"/> (cookie times
    /// are kept to the second: a session issued in the same second is refreshed once more, never missed).
    /// </summary>
    public bool HasChangedSince(Guid userId, DateTimeOffset? issuedUtc) =>
        _changes.TryGetValue(userId, out var changedAt) && (issuedUtc is null || issuedUtc <= changedAt);
}

/// <summary>
/// The security stamp validation of the authentication cookie, run at once (instead of after the validation interval)
/// for a session whose user's claims changed since the cookie was issued: the claims are rebuilt from the database and
/// the cookie renewed, without signing in again. A changed stamp or a locked-out account ends the session.
/// </summary>
internal sealed partial class CarRentalSecurityStampValidator(
    IOptions<SecurityStampValidatorOptions> options,
    SignInManager<ApplicationUser> signInManager,
    ILoggerFactory loggerFactory,
    UserSessionRegistry sessions)
    : SecurityStampValidator<ApplicationUser>(options, signInManager, loggerFactory)
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<CarRentalSecurityStampValidator>();

    public override async Task ValidateAsync(CookieValidatePrincipalContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Principal?.GetUserId() is not { } userId || !sessions.HasChangedSince(userId, context.Properties.IssuedUtc))
        {
            await base.ValidateAsync(context);
            return;
        }

        // The periodic validation, now. The issue date of the ticket stays: the cookie handler only renews a ticket that
        // has its issue and expiry dates, and the renewed cookie must carry the rebuilt principal.
        var user = await VerifySecurityStamp(context.Principal);
        if (user is not null)
        {
            await SecurityStampVerified(user, context);
            return;
        }

        LogSessionEnded(userId);
        context.RejectPrincipal();
        await SignInManager.SignOutAsync();
        await SignInManager.Context.SignOutAsync(IdentityConstants.TwoFactorRememberMeScheme);
    }

    /// <summary>
    /// The stamp of the session still matches and the account is not locked out: an automatic lockout (failed attempts)
    /// leaves the stamp unchanged, yet ends the sessions at their next validation, as it ends the circuits.
    /// </summary>
    protected override async Task<ApplicationUser?> VerifySecurityStamp(ClaimsPrincipal? principal)
    {
        var user = await base.VerifySecurityStamp(principal);
        return user is not null && await SignInManager.UserManager.IsLockedOutAsync(user) ? null : user;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Session of user {UserId} ended at its revalidation (stamp changed or account locked).")]
    private partial void LogSessionEnded(Guid userId);
}

/// <summary>Claims of a session that must survive the refresh of its principal.</summary>
internal static class SessionClaims
{
    /// <summary>
    /// The security stamp validator rebuilds the principal from the database, which loses how the user signed in: the
    /// <c>amr</c> claim (<c>mfa</c> after the authenticator code) and the authentication method are copied over, otherwise
    /// every refresh would close the back office (MFA policy, ADR-0014).
    /// </summary>
    public static Task KeepAuthenticationMethodAsync(SecurityStampRefreshingPrincipalContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.CurrentPrincipal is not { } current || context.NewPrincipal?.Identity is not ClaimsIdentity refreshed)
        {
            return Task.CompletedTask;
        }

        var kept = current.FindAll(MfaRequirement.AuthenticationMethodClaimType).Concat(current.FindAll(ClaimTypes.AuthenticationMethod));
        foreach (var claim in kept.Where(c => !refreshed.HasClaim(c.Type, c.Value)).ToList())
        {
            refreshed.AddClaim(new Claim(claim.Type, claim.Value));
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Whether the session of <paramref name="principal"/> may go on: the user exists and is not locked out, the security
    /// stamp is unchanged, and no permission of the session was revoked (granted ones appear at the next page load).
    /// </summary>
    public static async Task<bool> IsStillValidAsync(
        UserManager<ApplicationUser> userManager,
        IUserClaimsPrincipalFactory<ApplicationUser> claimsFactory,
        ClaimsPrincipal principal,
        IdentityOptions options)
    {
        ArgumentNullException.ThrowIfNull(userManager);
        ArgumentNullException.ThrowIfNull(claimsFactory);
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(options);

        var user = await userManager.GetUserAsync(principal);
        if (user is null || await userManager.IsLockedOutAsync(user))
        {
            return false;
        }

        if (userManager.SupportsUserSecurityStamp)
        {
            var principalStamp = principal.FindFirstValue(options.ClaimsIdentity.SecurityStampClaimType);
            if (!string.Equals(principalStamp, await userManager.GetSecurityStampAsync(user), StringComparison.Ordinal))
            {
                return false;
            }
        }

        var current = await claimsFactory.CreateAsync(user);
        return principal.FindAll(Permissions.ClaimType).All(c => current.HasClaim(Permissions.ClaimType, c.Value));
    }
}
