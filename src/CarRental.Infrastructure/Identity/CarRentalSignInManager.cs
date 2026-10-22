using CarRental.Application.Referential.Contracts;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CarRental.Infrastructure.Identity;

/// <summary>
/// The sign-in of the platform: on top of the checks of ASP.NET Core Identity, a member of a suspended agency cannot
/// sign in (<see cref="SignInResult.NotAllowed"/>), and the security stamp validation ends their open sessions
/// (ADR-0067). The support belongs to no agency and is never refused for that reason.
/// </summary>
public sealed class CarRentalSignInManager(
    UserManager<ApplicationUser> userManager,
    IHttpContextAccessor contextAccessor,
    IUserClaimsPrincipalFactory<ApplicationUser> claimsFactory,
    IOptions<IdentityOptions> optionsAccessor,
    ILogger<SignInManager<ApplicationUser>> logger,
    IAuthenticationSchemeProvider schemes,
    IUserConfirmation<ApplicationUser> confirmation,
    IAgencyDirectory agencies)
    : SignInManager<ApplicationUser>(userManager, contextAccessor, claimsFactory, optionsAccessor, logger, schemes, confirmation)
{
    public override async Task<bool> CanSignInAsync(ApplicationUser user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return await base.CanSignInAsync(user) && await IsAgencyOpenAsync(user, agencies);
    }

    /// <summary>True for the support, and for a member of an agency that exists and is not suspended.</summary>
    public static async Task<bool> IsAgencyOpenAsync(ApplicationUser user, IAgencyDirectory agencies, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(agencies);
        return user.AgencyId is not { } agencyId || await agencies.FindAsync(agencyId, cancellationToken) is { IsActive: true };
    }
}
