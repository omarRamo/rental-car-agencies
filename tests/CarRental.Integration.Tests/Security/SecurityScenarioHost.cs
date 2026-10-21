using CarRental.Infrastructure.Identity;
using CarRental.Testing;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace CarRental.Integration.Tests.Security;

/// <summary>
/// A host of its own for the security scenarios that need low limits (ADR-0062): <see cref="Limit"/> account form
/// submissions, health probes and category photos per client address, and staff sessions of <see cref="SessionLifetime"/>.
/// Scenarios use their own client address (CF-Connecting-IP), hence their own partitions, and their own staff accounts.
/// </summary>
/// <remarks>
/// It has its own database, like the public scenario host: on the shared one, its outbox pump and job server would
/// process the messages of the other tests.
/// </remarks>
public sealed class SecurityScenarioHost(IntegrationHost host) : IAsyncLifetime
{
    public const string Database = "CarRental_Security";

    public const int Limit = 3;

    public static readonly TimeSpan SessionLifetime = TimeSpan.FromSeconds(8);

    public CarRentalAppFactory Factory { get; } = new(host.Containers, Database)
    {
        RateLimitPermits = Limit,
        AccountRateLimitPermits = Limit,
        HealthRateLimitPermits = Limit,
        SessionAbsoluteLifetime = SessionLifetime,
    };

    public ValueTask InitializeAsync()
    {
        _ = Factory.Server;
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await Factory.DisposeAsync();

    /// <summary>An HTTP client keeping cookies, without following redirects, seen as coming from <paramref name="clientIp"/>.</summary>
    public HttpClient CreateClient(string clientIp)
    {
        var client = Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        client.DefaultRequestHeaders.Add("CF-Connecting-IP", clientIp);
        client.DefaultRequestHeaders.Add("Accept-Language", "fr");
        return client;
    }

    /// <summary>A staff account of this host's database with the test password and authenticator key.</summary>
    public async Task<string> CreateStaffAsync(string role)
    {
        var email = $"{role.ToLowerInvariant()}-{Guid.NewGuid():N}@tests.local";
        await using var scope = Factory.Services.CreateAsyncScope();
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
        var keys = (IUserAuthenticatorKeyStore<ApplicationUser>)scope.ServiceProvider.GetRequiredService<IUserStore<ApplicationUser>>();
        await keys.SetAuthenticatorKeyAsync(user, TestAccounts.AuthenticatorKey, CancellationToken.None);
        (await users.SetTwoFactorEnabledAsync(user, true)).Succeeded.ShouldBeTrue();
        return email;
    }
}

/// <summary>The classes that share the <see cref="SecurityScenarioHost"/>.</summary>
[CollectionDefinition(Name)]
public sealed class SecurityScenarioCollection : ICollectionFixture<SecurityScenarioHost>
{
    public const string Name = "Security scenarios";
}
