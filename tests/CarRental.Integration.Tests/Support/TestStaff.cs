using CarRental.Infrastructure.Identity;
using CarRental.Testing;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace CarRental.Integration.Tests.Support;

/// <summary>Staff accounts of their own for HTTP tests (tests run in parallel and must not share a user row).</summary>
internal static class TestStaff
{
    /// <summary>
    /// A new account of <paramref name="role"/> with the test password and, unless <paramref name="twoFactor"/> is false,
    /// two-factor authentication enabled on the test authenticator key.
    /// </summary>
    public static async Task<(Guid Id, string Email)> CreateAsync(IntegrationHost host, string role, bool twoFactor = true)
    {
        ArgumentNullException.ThrowIfNull(host);
        var email = $"{role.ToLowerInvariant()}-{Guid.NewGuid():N}@tests.local";
        await using var scope = host.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = new ApplicationUser
        {
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            DisplayName = $"{role} {Guid.NewGuid():N}",
            LockoutEnabled = true,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };
        (await users.CreateAsync(user, TestAccounts.Password)).Succeeded.ShouldBeTrue();
        (await users.AddToRoleAsync(user, role)).Succeeded.ShouldBeTrue();
        if (twoFactor)
        {
            var keys = (IUserAuthenticatorKeyStore<ApplicationUser>)scope.ServiceProvider.GetRequiredService<IUserStore<ApplicationUser>>();
            await keys.SetAuthenticatorKeyAsync(user, TestAccounts.AuthenticatorKey, CancellationToken.None);
            (await users.SetTwoFactorEnabledAsync(user, true)).Succeeded.ShouldBeTrue();
        }

        return (user.Id, email);
    }
}
