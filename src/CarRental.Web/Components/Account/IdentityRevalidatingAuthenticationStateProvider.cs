using CarRental.Infrastructure.Identity;
using CarRental.Web.Security;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace CarRental.Web.Components.Account;

/// <summary>
/// Revalidates the user of an interactive circuit every two minutes: a changed security stamp (password change, lock by
/// the manager, role change), a lockout or a revoked permission ends the authentication state of the circuit; the page
/// then reloads over HTTP (<c>NotAuthorizedRedirect</c>), where the cookie is validated with the current claims.
/// </summary>
internal sealed class IdentityRevalidatingAuthenticationStateProvider(
    ILoggerFactory loggerFactory,
    IServiceScopeFactory scopeFactory,
    IOptions<IdentityOptions> options)
    : RevalidatingServerAuthenticationStateProvider(loggerFactory)
{
    protected override TimeSpan RevalidationInterval => TimeSpan.FromMinutes(2);

    protected override async Task<bool> ValidateAuthenticationStateAsync(
        AuthenticationState authenticationState,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authenticationState);
        await using var scope = scopeFactory.CreateAsyncScope();
        return await SessionClaims.IsStillValidAsync(
            scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>(),
            scope.ServiceProvider.GetRequiredService<IUserClaimsPrincipalFactory<ApplicationUser>>(),
            authenticationState.User,
            options.Value);
    }
}
