using System.Security.Claims;
using CarRental.Application.Common.Seeding;
using CarRental.Application.Identity.Contracts;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CarRental.Infrastructure.Identity;

/// <summary>
/// Creates the roles (the four agency roles and <see cref="Roles.Support"/>) and reconciles their permission claims with
/// <see cref="RolePresets"/> at every start (all environments). Per-user extra permissions (user claims) are never touched.
/// </summary>
internal sealed partial class RolePresetSynchronizer(RoleManager<ApplicationRole> roleManager, ILogger<RolePresetSynchronizer> logger) : IDataSeeder
{
    public SeedStage Stage => SeedStage.Reference;

    public int Order => 100;

    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        foreach (var roleName in Roles.All)
        {
            var role = await roleManager.FindByNameAsync(roleName);
            if (role is null)
            {
                role = new ApplicationRole(roleName);
                EnsureSucceeded(await roleManager.CreateAsync(role), roleName);
                LogRoleCreated(roleName);
            }

            var claims = await roleManager.GetClaimsAsync(role);
            var current = claims.Where(c => c.Type == Permissions.ClaimType).ToList();
            var expected = RolePresets.PermissionsOf(roleName);

            foreach (var permission in expected.Where(p => !current.Any(c => c.Value == p)).OrderBy(p => p, StringComparer.Ordinal))
            {
                EnsureSucceeded(await roleManager.AddClaimAsync(role, new Claim(Permissions.ClaimType, permission)), roleName);
            }

            foreach (var obsolete in current.Where(c => !expected.Contains(c.Value)))
            {
                EnsureSucceeded(await roleManager.RemoveClaimAsync(role, obsolete), roleName);
                LogPermissionRemoved(obsolete.Value, roleName);
            }
        }
    }

    private static void EnsureSucceeded(IdentityResult result, string roleName)
    {
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                $"Role '{roleName}' could not be reconciled: {string.Join(" ", result.Errors.Select(e => e.Description))}");
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Role {Role} created.")]
    private partial void LogRoleCreated(string role);

    [LoggerMessage(Level = LogLevel.Information, Message = "Permission {Permission} removed from role {Role} (no longer in its preset).")]
    private partial void LogPermissionRemoved(string permission, string role);
}

public sealed class IdentityBootstrapOptions
{
    public const string SectionName = "Identity";

    /// <summary>
    /// E-mail of the first support account (the platform operator, ADR-0067), created (without password) at startup when
    /// no support account exists. The person then uses "Forgot password" on the login page, enrolls an authenticator at
    /// first sign-in, registers the agencies and invites their managers.
    /// </summary>
    public string? InitialSupportEmail { get; set; }
}

/// <summary>Production bootstrap: the first support account, when configured and no support account exists yet.</summary>
internal sealed partial class InitialSupportSeeder(
    UserManager<ApplicationUser> userManager,
    IOptions<IdentityBootstrapOptions> options,
    TimeProvider timeProvider,
    ILogger<InitialSupportSeeder> logger) : IDataSeeder
{
    public SeedStage Stage => SeedStage.Reference;

    public int Order => 110;

    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        var email = options.Value.InitialSupportEmail?.Trim();
        if (string.IsNullOrEmpty(email) || (await userManager.GetUsersInRoleAsync(Roles.Support)).Count > 0)
        {
            return;
        }

        var user = await userManager.FindByEmailAsync(email);
        if (user is null)
        {
            user = new ApplicationUser
            {
                UserName = email,
                Email = email,
                EmailConfirmed = true,
                DisplayName = email,
                LockoutEnabled = true,
                CreatedAtUtc = timeProvider.GetUtcNow(),
            };
            var created = await userManager.CreateAsync(user);
            if (!created.Succeeded)
            {
                throw new InvalidOperationException($"The initial support account could not be created: {string.Join(" ", created.Errors.Select(e => e.Description))}");
            }
        }
        else if (user.AgencyId is not null)
        {
            throw new InvalidOperationException("Identity:InitialSupportEmail belongs to a member of an agency: choose another e-mail for the support.");
        }

        await userManager.AddToRoleAsync(user, Roles.Support);
        LogInitialSupport(email);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Initial support account {Email} created without password: use 'Forgot password' on /account/login to set it.")]
    private partial void LogInitialSupport(string email);
}
