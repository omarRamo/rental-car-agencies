using System.Net;
using CarRental.Domain.Identity;
using CarRental.Infrastructure.Identity;
using CarRental.Infrastructure.Persistence;
using CarRental.Integration.Tests.Support;
using CarRental.Testing;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CarRental.Integration.Tests;

/// <summary>Mandatory two-factor authentication and permission policies on the back office.</summary>
public sealed class AuthenticationTests(IntegrationHost host)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Anonymous_users_are_sent_to_the_login_page()
    {
        using var client = host.CreateClient();

        var response = await client.GetAsync(new Uri("/admin", UriKind.Relative), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        HttpSignIn.PathAndQuery(response.Headers.Location!).ShouldContain("/account/login");
    }

    [Fact]
    public async Task Manager_signs_in_with_password_and_totp_and_reaches_the_back_office()
    {
        using var client = host.CreateClient();

        await HttpSignIn.SignInWithTwoFactorAsync(client, TestAccounts.ManagerEmail, "/admin", Ct);
        var dashboard = await client.GetAsync(new Uri("/admin", UriKind.Relative), Ct);
        var users = await client.GetAsync(new Uri("/admin/users", UriKind.Relative), Ct);
        var audit = await client.GetAsync(new Uri("/admin/audit", UriKind.Relative), Ct);

        dashboard.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await dashboard.Content.ReadAsStringAsync(Ct)).ShouldContain("MudBlazor");
        users.StatusCode.ShouldBe(HttpStatusCode.OK);
        audit.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Successful_sign_in_is_written_to_the_audit_log()
    {
        using var client = host.CreateClient();
        client.DefaultRequestHeaders.Add("CF-Connecting-IP", "203.0.113.50");
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);

        await HttpSignIn.SignInWithTwoFactorAsync(client, TestAccounts.AgentEmail, "/admin", Ct);

        await using var scope = host.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CarRentalDbContext>();
        var agent = await db.Users.SingleAsync(u => u.Email == TestAccounts.AgentEmail, Ct);
        var entry = await db.Set<AuditLog>()
            .Where(a => a.Action == AuditActions.LoginSucceeded && a.UserId == agent.Id && a.OccurredAtUtc >= before)
            .OrderByDescending(a => a.Id)
            .FirstOrDefaultAsync(Ct);

        entry.ShouldNotBeNull();
        entry.Details.ShouldBe("password + authenticator");
        entry.UserName.ShouldBe(TestAccounts.AgentEmail);
        entry.IpAddress.ShouldBe("203.0.113.50");
        entry.CorrelationId.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task A_wrong_authenticator_code_is_rejected_and_audited()
    {
        using var client = host.CreateClient();
        var twoFactor = await HttpSignIn.SubmitPasswordAsync(client, TestAccounts.AccountantEmail, TestAccounts.Password, "/admin", Ct);

        var response = await HttpSignIn.SubmitAuthenticatorCodeAsync(client, twoFactor, "000000", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.GetAsync(new Uri("/admin", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.Redirect);
        await using var scope = host.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CarRentalDbContext>();
        (await db.Set<AuditLog>().AnyAsync(a => a.Action == AuditActions.LoginFailed && a.Details == "invalid authenticator code", Ct)).ShouldBeTrue();
    }

    [Fact]
    public async Task A_user_without_two_factor_is_forced_to_enroll_before_the_back_office()
    {
        var email = $"no2fa-{Guid.NewGuid():N}@demo.local";
        await using (var scope = host.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true, DisplayName = "Sans 2FA", CreatedAtUtc = DateTimeOffset.UtcNow };
            (await users.CreateAsync(user, TestAccounts.Password)).Succeeded.ShouldBeTrue();
            (await users.AddToRoleAsync(user, "Manager")).Succeeded.ShouldBeTrue();
        }

        using var client = host.CreateClient();
        var afterPassword = await HttpSignIn.SubmitPasswordAsync(client, email, TestAccounts.Password, "/admin/users", Ct);
        afterPassword.OriginalString.ShouldBe("/admin/users");

        var admin = await client.GetAsync(new Uri("/admin/users", UriKind.Relative), Ct);

        admin.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        HttpSignIn.PathAndQuery(admin.Headers.Location!).ShouldStartWith("/account/manage/enable-authenticator");
        (await client.GetAsync(new Uri("/account/manage/enable-authenticator", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task The_accountant_is_denied_user_administration()
    {
        using var client = host.CreateClient();
        await HttpSignIn.SignInWithTwoFactorAsync(client, TestAccounts.AccountantEmail, "/admin", Ct);

        var users = await client.GetAsync(new Uri("/admin/users", UriKind.Relative), Ct);
        var audit = await client.GetAsync(new Uri("/admin/audit", UriKind.Relative), Ct);
        var jobs = await client.GetAsync(new Uri("/admin/jobs", UriKind.Relative), Ct);
        var invoices = await client.GetAsync(new Uri("/admin/invoices", UriKind.Relative), Ct);

        users.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        HttpSignIn.PathAndQuery(users.Headers.Location!).ShouldContain("/account/access-denied");
        HttpSignIn.PathAndQuery(audit.Headers.Location!).ShouldContain("/account/access-denied");
        HttpSignIn.PathAndQuery(jobs.Headers.Location!).ShouldContain("/account/access-denied");
        invoices.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Only_the_support_opens_the_jobs_dashboard()
    {
        // The background jobs of every agency: not even an agency's manager sees them (ADR-0067).
        using var manager = host.CreateClient();
        await HttpSignIn.SignInWithTwoFactorAsync(manager, TestAccounts.ManagerEmail, "/admin", Ct);
        var denied = await manager.GetAsync(new Uri("/admin/jobs", UriKind.Relative), Ct);
        denied.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        HttpSignIn.PathAndQuery(denied.Headers.Location!).ShouldContain("/account/access-denied");

        using var client = host.CreateClient();
        await HttpSignIn.SignInWithTwoFactorAsync(client, TestAccounts.SupportEmail, "/admin", Ct);

        var jobs = await client.GetAsync(new Uri("/admin/jobs", UriKind.Relative), Ct);

        jobs.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Sign_out_ends_the_session_and_is_audited()
    {
        using var client = host.CreateClient();
        await HttpSignIn.SignInWithTwoFactorAsync(client, TestAccounts.ManagerEmail, "/admin", Ct);
        var page = await client.GetStringAsync(new Uri("/account/manage", UriKind.Relative), Ct);
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = HttpSignIn.AntiforgeryToken(page),
            ["returnUrl"] = "/account/login",
        });

        var logout = await client.PostAsync(new Uri("/account/logout", UriKind.Relative), form, Ct);

        logout.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        (await client.GetAsync(new Uri("/admin", UriKind.Relative), Ct)).Headers.Location!.ToString().ShouldContain("/account/login");
        await using var scope = host.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CarRentalDbContext>();
        (await db.Set<AuditLog>().AnyAsync(a => a.Action == AuditActions.Logout && a.UserName == TestAccounts.ManagerEmail, Ct)).ShouldBeTrue();
    }
}
