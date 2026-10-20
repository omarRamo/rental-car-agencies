using System.Security.Claims;
using CarRental.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CarRental.Infrastructure.Identity;

/// <summary>Claim types added to the sign-in principal.</summary>
public static class CarRentalClaimTypes
{
    public const string DisplayName = "display_name";
}

/// <summary>Adds the display name to the principal (role and permission claims are added by the base factory).</summary>
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
            .AddSignInManager()
            .AddDefaultTokenProviders()
            .AddClaimsPrincipalFactory<CarRentalUserClaimsPrincipalFactory>();
    }
}
