using CarRental.Infrastructure.Identity;
using CarRental.Testing;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace CarRental.Integration.Tests.Fleet;

/// <summary>
/// Dedicated staff accounts of the fleet tests (test password and TOTP key), so that they never sign in at the same
/// time as other test classes with the shared demo accounts (concurrent sign-ins of one account race on its row).
/// </summary>
internal static class FleetAccounts
{
    public const string Manager = "gerant-flotte-it@demo.local";
    public const string Accountant = "comptable-flotte-it@demo.local";

    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static async Task EnsureAsync(IntegrationHost host, string email, string role)
    {
        await Gate.WaitAsync();
        try
        {
            await using var scope = host.CreateScope();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            if (await users.FindByEmailAsync(email) is not null)
            {
                return;
            }

            var user = new ApplicationUser
            {
                UserName = email,
                Email = email,
                EmailConfirmed = true,
                DisplayName = email,
                LockoutEnabled = true,
                CreatedAtUtc = DateTimeOffset.UtcNow,
            };
            Ensure(await users.CreateAsync(user, TestAccounts.Password));
            Ensure(await users.AddToRoleAsync(user, role));
            var keys = (IUserAuthenticatorKeyStore<ApplicationUser>)scope.ServiceProvider.GetRequiredService<IUserStore<ApplicationUser>>();
            await keys.SetAuthenticatorKeyAsync(user, TestAccounts.AuthenticatorKey, CancellationToken.None);
            Ensure(await users.SetTwoFactorEnabledAsync(user, true));
        }
        finally
        {
            Gate.Release();
        }
    }

    private static void Ensure(IdentityResult result) =>
        result.Succeeded.ShouldBeTrue(string.Join(" ", result.Errors.Select(e => e.Description)));
}
