using System.Security.Claims;
using CarRental.Application.Identity.Contracts;
using CarRental.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CarRental.Infrastructure.Identity;

/// <summary>Claim types added to the sign-in principal.</summary>
public static class CarRentalClaimTypes
{
    public const string DisplayName = "display_name";

    /// <summary>
    /// The agency the session works for (ADR-0067): the agency of a staff member, or the agency the support entered.
    /// Absent for the support outside any agency.
    /// </summary>
    public const string Agency = "agency";
}

/// <summary>
/// The sign-in principal: the base factory adds the roles and the permission claims of the role presets (role claims)
/// and of the user's extra permissions (user claims); this factory adds the display name, the agency the session works
/// for (<see cref="CarRentalClaimTypes.Agency"/>) and the permissions that those permissions include
/// (<see cref="Permissions.WithImplied"/>), so that an agent granted <c>pricing.manage</c> also reads the pricing screen.
/// A member of an agency never gets a platform permission, even granted by mistake (ADR-0067).
/// </summary>
public sealed class CarRentalUserClaimsPrincipalFactory(
    UserManager<ApplicationUser> userManager,
    RoleManager<ApplicationRole> roleManager,
    IOptions<IdentityOptions> options)
    : UserClaimsPrincipalFactory<ApplicationUser, ApplicationRole>(userManager, roleManager, options)
{
    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(ApplicationUser user)
    {
        ArgumentNullException.ThrowIfNull(user);
        var identity = await base.GenerateClaimsAsync(user);
        if (!string.IsNullOrWhiteSpace(user.DisplayName))
        {
            identity.AddClaim(new Claim(CarRentalClaimTypes.DisplayName, user.DisplayName));
        }

        if (user.CurrentAgencyId is { } agencyId)
        {
            identity.AddClaim(new Claim(CarRentalClaimTypes.Agency, agencyId.ToString("D")));
        }

        if (user.AgencyId is not null)
        {
            foreach (var platform in identity.FindAll(c => c.Type == Permissions.ClaimType && Permissions.PlatformOnly.Contains(c.Value)).ToList())
            {
                identity.RemoveClaim(platform);
            }
        }

        var granted = identity.FindAll(Permissions.ClaimType).Select(c => c.Value).ToHashSet(StringComparer.Ordinal);
        foreach (var implied in Permissions.WithImplied(granted).Except(granted, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            identity.AddClaim(new Claim(Permissions.ClaimType, implied));
        }

        return identity;
    }
}

public static class IdentityServiceCollectionExtensions
{
    /// <summary>
    /// ASP.NET Core Identity on <see cref="CarRentalDbContext"/>: unique e-mails, 12-character passwords, lockout after
    /// 5 failures for 15 minutes, default token providers (authenticator, password reset). Cookies are set up by the web host.
    /// </summary>
    public static IdentityBuilder AddCarRentalIdentityCore(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services
            .AddIdentityCore<ApplicationUser>(options =>
            {
                options.User.RequireUniqueEmail = true;
                options.Password.RequiredLength = 12;
                options.Password.RequireDigit = true;
                options.Password.RequireLowercase = true;
                options.Password.RequireUppercase = true;
                options.Password.RequireNonAlphanumeric = false;
                options.Lockout.AllowedForNewUsers = true;
                options.Lockout.MaxFailedAccessAttempts = 5;
                options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
                options.SignIn.RequireConfirmedAccount = false;
                options.SignIn.RequireConfirmedEmail = false;
            })
            .AddRoles<ApplicationRole>()
            .AddEntityFrameworkStores<CarRentalDbContext>()
            .AddSignInManager<CarRentalSignInManager>()
            .AddDefaultTokenProviders()
            .AddClaimsPrincipalFactory<CarRentalUserClaimsPrincipalFactory>();
    }
}
