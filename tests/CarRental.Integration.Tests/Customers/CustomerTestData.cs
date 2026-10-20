using System.Globalization;
using System.Security.Cryptography;
using CarRental.Application.Common.Security;
using CarRental.Application.Customers.Contracts;
using CarRental.Application.Identity.Contracts;
using CarRental.Infrastructure.Identity;
using CarRental.Testing;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace CarRental.Integration.Tests.Customers;

/// <summary>Unique data per test (tests run in parallel on one database) and actors with the preset permissions.</summary>
internal static class CustomerTestData
{
    private const string Letters = "abcdefghijklmnopqrstuvwxyz";

    /// <summary>A unique name made of letters only (names reject digits), e.g. "Kqzvtrma".</summary>
    public static string Name()
    {
        Span<char> name = stackalloc char[10];
        for (var i = 0; i < name.Length; i++)
        {
            name[i] = Letters[RandomNumberGenerator.GetInt32(Letters.Length)];
        }

        name[0] = char.ToUpperInvariant(name[0]);
        return new string(name);
    }

    /// <summary>A random Tunisian mobile number, 8 digits starting with 2 to 9.</summary>
    public static string TunisianPhone() =>
        RandomNumberGenerator.GetInt32(20_000_000, 100_000_000).ToString(CultureInfo.InvariantCulture);

    /// <summary>A random Tunisian national ID number (8 digits).</summary>
    public static string Cin() => RandomNumberGenerator.GetInt32(0, 100_000_000).ToString("D8", CultureInfo.InvariantCulture);

    public static IdentityDocumentInput CinInput(string number) =>
        new(IdentityDocumentTypeInfo.NationalId, number, "TN", new DateOnly(2016, 3, 1));

    /// <summary>A tiny valid PNG (1×1 pixel).</summary>
    public static byte[] Png() => Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    public static async Task<UserContextSnapshot> ActorAsync(IntegrationHost host, string email, string role)
    {
        await using var scope = host.CreateScope();
        var user = await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().FindByEmailAsync(email);
        return UserContextSnapshot.ForUser(user!.Id, email, RolePresets.PermissionsOf(role), "198.51.100.7", $"corr-{Guid.NewGuid():N}");
    }

    /// <summary>
    /// A new staff account of <paramref name="role"/> with two-factor authentication on the test key: HTTP tests sign in
    /// with their own account, so that parallel sign-ins never update the same user row.
    /// </summary>
    public static async Task<string> NewStaffAsync(IntegrationHost host, string role)
    {
        var email = $"{role.ToLowerInvariant()}-{Guid.NewGuid():N}@tests.local";
        await using var scope = host.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = new ApplicationUser
        {
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            DisplayName = $"{role} {Name()}",
            LockoutEnabled = true,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };
        (await users.CreateAsync(user, TestAccounts.Password)).Succeeded.ShouldBeTrue();
        (await users.AddToRoleAsync(user, role)).Succeeded.ShouldBeTrue();
        var keys = (IUserAuthenticatorKeyStore<ApplicationUser>)scope.ServiceProvider.GetRequiredService<IUserStore<ApplicationUser>>();
        await keys.SetAuthenticatorKeyAsync(user, TestAccounts.AuthenticatorKey, CancellationToken.None);
        (await users.SetTwoFactorEnabledAsync(user, true)).Succeeded.ShouldBeTrue();
        return email;
    }

    public static Task<UserContextSnapshot> AgentAsync(IntegrationHost host) => ActorAsync(host, TestAccounts.AgentEmail, Roles.Agent);

    public static Task<UserContextSnapshot> ManagerAsync(IntegrationHost host) => ActorAsync(host, TestAccounts.ManagerEmail, Roles.Manager);

    /// <summary>A Tunisian with a CIN carrying a front image (uploaded to the storage), created by the agent.</summary>
    public static async Task<(Guid PersonId, Guid DocumentId)> PersonWithCinImageAsync(IntegrationHost host, CancellationToken cancellationToken)
    {
        var agent = await AgentAsync(host);
        await using var scope = host.CreateScope(agent);
        var registration = scope.ServiceProvider.GetRequiredService<ICustomerRegistration>();
        var person = await registration.FindOrCreatePersonAsync(
            new PersonRegistrationRequest(Name(), Name(), "TN", new DateOnly(1990, 1, 1), TunisianPhone()),
            cancellationToken);
        person.IsSuccess.ShouldBeTrue(person.ToString());
        using var front = new MemoryStream(Png());
        var document = await registration.AddOrReplaceDocumentAsync(person.Value.PersonId, CinInput(Cin()), front, cancellationToken: cancellationToken);
        document.IsSuccess.ShouldBeTrue(document.ToString());
        return (person.Value.PersonId, document.Value);
    }
}
