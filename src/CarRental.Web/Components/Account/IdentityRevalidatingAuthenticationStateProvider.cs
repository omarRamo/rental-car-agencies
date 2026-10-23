using CarRental.Application.Identity.Users;
using CarRental.Application.Referential.Contracts;
using CarRental.Infrastructure.Identity;
using CarRental.Web.Security;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace CarRental.Web.Components.Account;

/// <summary>
/// Revalidates the user of an interactive circuit every two minutes: a changed security stamp (password change, lock by
/// the manager, role change), a lockout, a revoked permission, another agency entered or left by the support since the
/// circuit opened (ADR-0067) or the end of the absolute lifetime of the session (<see cref="StaffSessionOptions"/>) ends
/// the authentication state of the circuit; the page then reloads over HTTP
/// (<c>NotAuthorizedRedirect</c>), where the cookie is validated at once with the current state of the account: fresh
/// claims, or the login page.
/// </summary>
internal sealed class IdentityRevalidatingAuthenticationStateProvider(
    ILoggerFactory loggerFactory,
    IServiceScopeFactory scopeFactory,
    IUserSessions sessions,
    IOptions<IdentityOptions> options,
    IOptions<StaffSessionOptions> sessionOptions,
    TimeProvider timeProvider)
    : RevalidatingServerAuthenticationStateProvider(loggerFactory)
{
    protected override TimeSpan RevalidationInterval => TimeSpan.FromMinutes(2);

    protected override async Task<bool> ValidateAuthenticationStateAsync(
        AuthenticationState authenticationState,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authenticationState);
        await using var scope = scopeFactory.CreateAsyncScope();
        var valid = !SessionLifetime.HasExpired(authenticationState.User, timeProvider.GetUtcNow(), sessionOptions.Value.AbsoluteLifetime)
            && await SessionClaims.IsStillValidAsync(
                scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>(),
                scope.ServiceProvider.GetRequiredService<IUserClaimsPrincipalFactory<ApplicationUser>>(),
                authenticationState.User,
                options.Value,
                scope.ServiceProvider.GetRequiredService<IAgencyDirectory>());
        if (!valid && authenticationState.User.GetUserId() is { } userId)
        {
            // The reload must not wait for the validation interval of the cookie.
            sessions.RefreshClaims(userId);
        }

        return valid;
    }
}
