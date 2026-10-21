using System.Net;
using CarRental.Application.Identity.Contracts;
using CarRental.Domain.Identity;
using CarRental.Infrastructure.Persistence;
using CarRental.Integration.Tests.Support;
using CarRental.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CarRental.Integration.Tests.Identity;

/// <summary>
/// Several sign-ins of one user at the same moment (two tabs, a double submission, a retry): every one of them must end
/// normally — never with an error page — and be audited.
/// </summary>
public sealed class ConcurrentSignInTests(IntegrationHost host)
{
    private const int ParallelSignIns = 8;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Parallel_sign_ins_of_one_user_all_succeed_and_are_all_audited()
    {
        var (userId, email) = await TestStaff.CreateAsync(host, Roles.Agent);
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);
        var clients = Enumerable.Range(0, ParallelSignIns).Select(_ => host.CreateClient()).ToList();
        try
        {
            var forms = await PrepareAuthenticatorFormsAsync(clients, email, Totp.Compute(TestAccounts.AuthenticatorKey));

            // Every authenticator step loads the same user row, then records the sign-in at the same moment.
            var responses = await Task.WhenAll(forms.Select(f => f.Client.PostAsync(f.Url, f.Form, Ct)));

            foreach (var response in responses)
            {
                response.StatusCode.ShouldBe(HttpStatusCode.Redirect, await response.Content.ReadAsStringAsync(Ct));
                HttpSignIn.PathAndQuery(response.Headers.Location!).ShouldBe("/admin");
            }

            foreach (var client in clients)
            {
                (await client.GetAsync(new Uri("/admin", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
            }

            await using var scope = host.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<CarRentalDbContext>();
            (await db.Set<AuditLog>().CountAsync(a => a.Action == AuditActions.LoginSucceeded && a.UserId == userId && a.OccurredAtUtc >= before, Ct))
                .ShouldBe(ParallelSignIns);
            var user = await db.Users.AsNoTracking().SingleAsync(u => u.Id == userId, Ct);
            user.LastSignInAtUtc.ShouldNotBeNull().ShouldBeGreaterThanOrEqualTo(before);
        }
        finally
        {
            DisposeAll(clients);
        }
    }

    [Fact]
    public async Task Parallel_wrong_codes_are_rejected_without_an_error_page_and_all_audited()
    {
        var (userId, email) = await TestStaff.CreateAsync(host, Roles.Agent);
        var clients = Enumerable.Range(0, 4).Select(_ => host.CreateClient()).ToList();
        try
        {
            var forms = await PrepareAuthenticatorFormsAsync(clients, email, "000000");

            // Each failure increments the same failed-access counter: concurrent increments conflict.
            var responses = await Task.WhenAll(forms.Select(f => f.Client.PostAsync(f.Url, f.Form, Ct)));

            foreach (var response in responses)
            {
                response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
            }

            await using var scope = host.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<CarRentalDbContext>();
            (await db.Set<AuditLog>().CountAsync(a => a.Action == AuditActions.LoginFailed && a.EntityId == userId.ToString(), Ct)).ShouldBe(clients.Count);
        }
        finally
        {
            DisposeAll(clients);
        }
    }

    /// <summary>Password step of every client, then the authenticator form of each one, ready to be posted.</summary>
    private static async Task<List<(HttpClient Client, Uri Url, FormUrlEncodedContent Form)>> PrepareAuthenticatorFormsAsync(
        List<HttpClient> clients,
        string email,
        string code)
    {
        var forms = new List<(HttpClient, Uri, FormUrlEncodedContent)>();
        foreach (var client in clients)
        {
            var twoFactor = await HttpSignIn.SubmitPasswordAsync(client, email, TestAccounts.Password, "/admin", Ct);
            twoFactor.OriginalString.ShouldStartWith("/account/login-with-2fa");
            var page = await client.GetStringAsync(twoFactor, Ct);
            forms.Add((client, twoFactor, new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = HttpSignIn.AntiforgeryToken(page),
                ["_handler"] = "login-with-2fa",
                ["Input.Code"] = code,
            })));
        }

        return forms;
    }

    private static void DisposeAll(List<HttpClient> clients)
    {
        foreach (var client in clients)
        {
            client.Dispose();
        }
    }
}
