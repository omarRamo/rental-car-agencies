using CarRental.Application.Common.Seeding;
using CarRental.Application.Identity.Contracts;
using CarRental.Infrastructure.Seeding;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CarRental.Infrastructure.Identity;

/// <summary>Demo accounts (development and recette only; <c>Seed:Demo=true</c>). They share the configured password and TOTP key.</summary>
public static class DemoUsers
{
    public const string ManagerEmail = "gerant@demo.local";
    public const string AgentEmail = "agent@demo.local";
    public const string AccountantEmail = "comptable@demo.local";

    public static IReadOnlyList<(string Email, string DisplayName, string Role)> All { get; } =
    [
        (ManagerEmail, "Gérant Démo", Roles.Manager),
        (AgentEmail, "Agent Démo", Roles.Agent),
        (AccountantEmail, "Comptable Démo", Roles.Accountant),
    ];
}

/// <summary>
/// Creates the demo users with two-factor authentication already enabled on a known authenticator key, so that
/// developers and automated tests can sign in. Skipped (with a warning) when the password or key is not configured.
/// </summary>
internal sealed partial class IdentityDemoSeeder(
    UserManager<ApplicationUser> userManager,
    IUserStore<ApplicationUser> userStore,
    IOptions<SeedOptions> options,
    IOptions<TwoFactorOptions> twoFactor,
    TimeProvider timeProvider,
    ILogger<IdentityDemoSeeder> logger) : IDataSeeder
{
    public SeedStage Stage => SeedStage.Demo;

    public int Order => 300;

    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (string.IsNullOrWhiteSpace(settings.DemoUserPassword) || string.IsNullOrWhiteSpace(settings.DemoAuthenticatorKey))
        {
            LogMissingSecrets();
            return;
        }

        var keyStore = (IUserAuthenticatorKeyStore<ApplicationUser>)userStore;
        foreach (var (email, displayName, role) in DemoUsers.All)
        {
            var user = await userManager.FindByEmailAsync(email);
            if (user is not null)
            {
                if (!await userManager.IsInRoleAsync(user, role))
                {
                    Ensure(await userManager.AddToRoleAsync(user, role), email);
                }

                continue;
            }

            user = new ApplicationUser
            {
                UserName = email,
                Email = email,
                EmailConfirmed = true,
                DisplayName = displayName,
                LockoutEnabled = true,
                CreatedAtUtc = timeProvider.GetUtcNow(),
            };
            Ensure(await userManager.CreateAsync(user, settings.DemoUserPassword), email);
            Ensure(await userManager.AddToRoleAsync(user, role), email);
            if (twoFactor.Value.Required)
            {
                await keyStore.SetAuthenticatorKeyAsync(user, settings.DemoAuthenticatorKey, cancellationToken);
                Ensure(await userManager.SetTwoFactorEnabledAsync(user, true), email);
            }

            LogCreated(email, role);
        }
    }

    private static void Ensure(IdentityResult result, string email)
    {
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                $"Demo user {email} could not be created: {string.Join(" ", result.Errors.Select(e => e.Description))}");
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Seed:Demo is on but Seed:DemoUserPassword or Seed:DemoAuthenticatorKey is missing: demo users are not created.")]
    private partial void LogMissingSecrets();

    [LoggerMessage(Level = LogLevel.Information, Message = "Demo user {Email} ({Role}) created.")]
    private partial void LogCreated(string email, string role);
}
