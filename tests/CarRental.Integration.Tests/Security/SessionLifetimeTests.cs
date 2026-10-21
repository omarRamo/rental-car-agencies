using System.Diagnostics;
using System.Net;
using CarRental.Application.Identity.Contracts;
using CarRental.Integration.Tests.Support;
using CarRental.Testing;

namespace CarRental.Integration.Tests.Security;

/// <summary>
/// However active it is, a staff session ends at the end of its absolute lifetime (ADR-0062, ASVS 3.3.2): the host of the
/// security scenarios uses <see cref="SecurityScenarioHost.SessionLifetime"/> instead of 12 hours.
/// </summary>
[Collection(SecurityScenarioCollection.Name)]
public sealed class SessionLifetimeTests(SecurityScenarioHost scenario)
{
    private static readonly Uri Admin = new("/admin", UriKind.Relative);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_active_session_ends_at_the_end_of_its_lifetime()
    {
        var email = await scenario.CreateStaffAsync(Roles.Agent);
        using var client = scenario.CreateClient("198.51.100.71");
        var twoFactor = await HttpSignIn.SubmitPasswordAsync(client, email, TestAccounts.Password, "/admin", Ct);
        (await HttpSignIn.SubmitAuthenticatorCodeAsync(client, twoFactor, Totp.Compute(TestAccounts.AuthenticatorKey), Ct)).StatusCode.ShouldBe(HttpStatusCode.Redirect);
        var signedIn = Stopwatch.StartNew();

        // Active all along: each request renews the sliding cookie, never the sign-in instant.
        while (signedIn.Elapsed < SecurityScenarioHost.SessionLifetime - TimeSpan.FromSeconds(3))
        {
            (await client.GetAsync(Admin, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
            await Task.Delay(TimeSpan.FromSeconds(1), Ct);
        }

        while (signedIn.Elapsed < SecurityScenarioHost.SessionLifetime + TimeSpan.FromSeconds(1))
        {
            await Task.Delay(TimeSpan.FromMilliseconds(250), Ct);
        }

        var expired = await client.GetAsync(Admin, Ct);
        var again = await client.GetAsync(Admin, Ct);

        expired.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        HttpSignIn.PathAndQuery(expired.Headers.Location!).ShouldContain("/account/login");
        again.StatusCode.ShouldBe(HttpStatusCode.Redirect, "the cookie was removed with the session");
    }
}
