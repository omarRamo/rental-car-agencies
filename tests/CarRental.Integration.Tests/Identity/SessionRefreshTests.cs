using System.Net;
using CarRental.Application.Identity.Contracts;
using CarRental.Application.Identity.Users;
using CarRental.Infrastructure.Identity;
using CarRental.Integration.Tests.Support;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;
using Microsoft.Extensions.DependencyInjection;

namespace CarRental.Integration.Tests.Identity;

/// <summary>
/// The security stamp validation rebuilds the claims of a session (every minute, or at once after a change of
/// permissions): the session stays an MFA session, and a changed stamp still ends it.
/// </summary>
public sealed class SessionRefreshTests(IntegrationHost host)
{
    private static readonly Uri Admin = new("/admin", UriKind.Relative);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_refreshed_session_keeps_its_two_factor_sign_in()
    {
        var (userId, email) = await TestStaff.CreateAsync(host, Roles.Agent);
        using var client = host.CreateClient();
        await HttpSignIn.SignInWithTwoFactorAsync(client, email, "/admin", Ct);

        host.Factory.Services.GetRequiredService<IUserSessions>().RefreshClaims(userId);
        var refreshed = await client.GetAsync(Admin, Ct);

        // The renewed cookie carries the rebuilt principal: the back office (MFA policy) stays open with it.
        refreshed.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.GetAsync(Admin, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.GetAsync(new Uri("/admin/bookings", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_refresh_renews_the_session_cookie_with_the_rebuilt_principal()
    {
        var (userId, email) = await TestStaff.CreateAsync(host, Roles.Agent);
        var cookies = new CookieContainer();
        using var client = host.Factory.CreateDefaultClient(new Uri("http://localhost"), new CookieContainerHandler(cookies));
        await HttpSignIn.SignInWithTwoFactorAsync(client, email, "/admin", Ct);
        var issued = AuthenticationCookies(cookies);

        host.Factory.Services.GetRequiredService<IUserSessions>().RefreshClaims(userId);
        (await client.GetAsync(Admin, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // Without a renewed cookie, the fresh claims would only live for that request.
        AuthenticationCookies(cookies).ShouldNotBe(issued);
        (await client.GetAsync(Admin, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_locked_out_account_ends_its_sessions_at_the_next_validation()
    {
        var (userId, email) = await TestStaff.CreateAsync(host, Roles.Agent);
        using var client = host.CreateClient();
        await HttpSignIn.SignInWithTwoFactorAsync(client, email, "/admin", Ct);
        await using (var scope = host.CreateScope())
        {
            // As after repeated failed attempts: locked out, security stamp unchanged.
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = (await users.FindByIdAsync(userId.ToString()))!;
            (await users.SetLockoutEndDateAsync(user, DateTimeOffset.UtcNow.AddMinutes(15))).Succeeded.ShouldBeTrue();
        }

        host.Factory.Services.GetRequiredService<IUserSessions>().RefreshClaims(userId);
        var response = await client.GetAsync(Admin, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        HttpSignIn.PathAndQuery(response.Headers.Location!).ShouldContain("/account/login");
    }

    [Fact]
    public async Task A_refresh_after_a_security_stamp_change_ends_the_session()
    {
        var (userId, email) = await TestStaff.CreateAsync(host, Roles.Agent);
        using var client = host.CreateClient();
        await HttpSignIn.SignInWithTwoFactorAsync(client, email, "/admin", Ct);
        await using (var scope = host.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            (await users.UpdateSecurityStampAsync((await users.FindByIdAsync(userId.ToString()))!)).Succeeded.ShouldBeTrue();
        }

        host.Factory.Services.GetRequiredService<IUserSessions>().RefreshClaims(userId);
        var response = await client.GetAsync(Admin, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        HttpSignIn.PathAndQuery(response.Headers.Location!).ShouldContain("/account/login");
    }

    /// <summary>The value of the authentication cookie (all its chunks).</summary>
    private static string AuthenticationCookies(CookieContainer cookies) =>
        string.Join('|', cookies.GetAllCookies().Where(c => c.Name.StartsWith("carrental.auth", StringComparison.Ordinal)).Select(c => $"{c.Name}={c.Value}"));
}
