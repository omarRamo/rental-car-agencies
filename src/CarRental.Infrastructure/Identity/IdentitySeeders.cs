using System.Security.Claims;
using CarRental.Application.Common.Seeding;
using CarRental.Application.Identity.Contracts;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CarRental.Infrastructure.Identity;

/// <summary>
/// Creates the four roles and reconciles their permission claims with <see cref="RolePresets"/> at every start
/// (all environments). Per-user extra permissions (user claims) are never touched.
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
    /// E-mail of the first manager, created (without password) at startup when no manager exists. The person then uses
    /// "Forgot password" on the login page and enrolls an authenticator at first sign-in.
    /// </summary>
    public string? InitialManagerEmail { get; set; }
}

/// <summary>Production bootstrap: the first manager account, when configured and no manager exists yet.</summary>
internal sealed partial class InitialManagerSeeder(
    UserManager<ApplicationUser> userManager,
    IOptions<IdentityBootstrapOptions> options,
    TimeProvider timeProvider,
    ILogger<InitialManagerSeeder> logger) : IDataSeeder
{
    public SeedStage Stage => SeedStage.Reference;

    public int Order => 110;

    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        var email = options.Value.InitialManagerEmail?.Trim();
        if (string.IsNullOrEmpty(email) || (await userManager.GetUsersInRoleAsync(Roles.Manager)).Count > 0)
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
                throw new InvalidOperationException($"The initial manager could not be created: {string.Join(" ", created.Errors.Select(e => e.Description))}");
            }
        }

        await userManager.AddToRoleAsync(user, Roles.Manager);
        LogInitialManager(email);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Initial manager {Email} created without password: use 'Forgot password' on /account/login to set it.")]
    private partial void LogInitialManager(string email);
}
