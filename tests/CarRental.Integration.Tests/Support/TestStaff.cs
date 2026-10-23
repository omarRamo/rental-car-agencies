using CarRental.Application.Common.Security;
using CarRental.Domain.Referential;
using CarRental.Infrastructure.Identity;
using CarRental.Infrastructure.Persistence;
using CarRental.Testing;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CarRental.Integration.Tests.Support;

/// <summary>Staff accounts of their own for HTTP tests (tests run in parallel and must not share a user row).</summary>
internal static class TestStaff
{
    /// <summary>
    /// A new account of <paramref name="role"/> with the test password and, unless <paramref name="twoFactor"/> is false,
    /// two-factor authentication enabled on the test authenticator key. A member of <paramref name="agencyId"/> (the
    /// main demo agency by default), or a support account for the role <c>Support</c> (ADR-0067).
    /// </summary>
    public static async Task<(Guid Id, string Email)> CreateAsync(IntegrationHost host, string role, bool twoFactor = true, Guid? agencyId = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        var support = role == CarRental.Application.Identity.Contracts.Roles.Support;
        var agency = support ? (Guid?)null : agencyId ?? await DemoAgencyIdAsync(host);
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
            AgencyId = agency,
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

    /// <summary>The main demo agency of the test host (its demo accounts belong to it).</summary>
    public static async Task<Guid> DemoAgencyIdAsync(IntegrationHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        await using var scope = host.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<CarRentalDbContext>().Set<Agency>()
            .Where(a => a.Name == DemoUsers.DemoAgencyName)
            .Select(a => a.Id)
            .SingleAsync();
    }

    /// <summary>The actor of an account: a member of its agency with the presets of <paramref name="role"/>, or the support.</summary>
    public static async Task<UserContextSnapshot> ActorAsync(IntegrationHost host, Guid userId, string email, string role, string? ipAddress = "198.51.100.40")
    {
        ArgumentNullException.ThrowIfNull(host);
        await using var scope = host.CreateScope();
        var agencyId = await scope.ServiceProvider.GetRequiredService<CarRentalDbContext>().Users
            .Where(u => u.Id == userId)
            .Select(u => u.AgencyId)
            .SingleAsync();
        return agencyId is { } agency
            ? TestActors.Staff(agency, role, email, userId, ipAddress, $"corr-{Guid.NewGuid():N}")
            : TestActors.Support(userName: email, userId: userId, ipAddress: ipAddress, correlationId: $"corr-{Guid.NewGuid():N}");
    }
}
