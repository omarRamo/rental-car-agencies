using System.Net;
using CarRental.Application.Identity.Contracts;
using CarRental.Infrastructure.Persistence;
using CarRental.Integration.Tests.Support;
using CarRental.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CarRental.Integration.Tests.Security;

/// <summary>
/// The platform pages over HTTP (ADR-0067): reserved to the support; the support outside any agency is led to them; entering
/// and leaving an agency (form posts) changes the agency of the session. The tests that change the agency a support account
/// entered use an account of their own: the shared demo support account must stay outside any agency for the others.
/// </summary>
public sealed class PlatformPagesTests(IntegrationHost host)
{
    private static readonly string[] PlatformPages =
    [
        "/admin/platform",
        "/admin/platform/support",
        "/admin/platform/settings",
        "/admin/platform/audit",
    ];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_manager_is_denied_every_platform_page_and_the_support_opens_them()
    {
        var agency = await TestAgencies.RegisterAsync(host, "Pages", Ct);
        var (_, managerEmail) = await TestStaff.CreateAsync(host, Roles.Manager);
        using var manager = host.CreateClient();
        await HttpSignIn.SignInWithTwoFactorAsync(manager, managerEmail, "/admin", Ct);

        foreach (var page in PlatformPages.Append($"/admin/platform/agencies/{agency.Id:D}"))
        {
            var denied = await manager.GetAsync(new Uri(page, UriKind.Relative), Ct);
            denied.StatusCode.ShouldBe(HttpStatusCode.Redirect, page);
            HttpSignIn.PathAndQuery(denied.Headers.Location!).ShouldContain("/account/access-denied", Case.Sensitive, page);
        }

        using var support = host.CreateClient();
        await HttpSignIn.SignInWithTwoFactorAsync(support, TestAccounts.SupportEmail, "/admin/platform", Ct);
        foreach (var page in PlatformPages.Append($"/admin/platform/agencies/{agency.Id:D}"))
        {
            (await support.GetAsync(new Uri(page, UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK, page);
        }
    }

    [Fact]
    public async Task A_manager_cannot_enter_an_agency()
    {
        var agency = await TestAgencies.RegisterAsync(host, "Entrée refusée", Ct);
        var (managerId, managerEmail) = await TestStaff.CreateAsync(host, Roles.Manager);
        using var client = host.CreateClient();
        await HttpSignIn.SignInWithTwoFactorAsync(client, managerEmail, "/admin", Ct);

        var response = await PostAsync(client, "/admin/platform/enter", agency.Id);

        response.StatusCode.ShouldNotBe(HttpStatusCode.OK);
        HttpSignIn.PathAndQuery(response.Headers.Location!).ShouldContain("/account/access-denied");
        await using var scope = host.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<CarRentalDbContext>().Users.SingleAsync(u => u.Id == managerId, Ct)).SelectedAgencyId.ShouldBeNull();
    }

    [Fact]
    public async Task The_support_outside_any_agency_is_led_to_the_platform_pages()
    {
        var (_, supportEmail) = await TestStaff.CreateAsync(host, Roles.Support);
        using var client = host.CreateClient();
        await HttpSignIn.SignInWithTwoFactorAsync(client, supportEmail, "/admin/platform", Ct);

        (await RedirectOfAsync(client, "/admin/fleet")).ShouldBe("/admin/platform");
        (await RedirectOfAsync(client, "/admin")).ShouldBe("/admin/platform");
        (await RedirectOfAsync(client, "/admin/settings")).ShouldBe("/admin/platform/settings");
        (await RedirectOfAsync(client, "/admin/audit")).ShouldBe("/admin/platform/audit");
        (await client.GetAsync(new Uri("/admin/outbox", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Entering_and_leaving_an_agency_changes_the_agency_of_the_session()
    {
        var agency = await TestAgencies.RegisterAsync(host, "Session", Ct);
        var (supportId, supportEmail) = await TestStaff.CreateAsync(host, Roles.Support);
        using var client = host.CreateClient();
        await HttpSignIn.SignInWithTwoFactorAsync(client, supportEmail, "/admin/platform", Ct);

        var entered = await PostAsync(client, "/admin/platform/enter", agency.Id);
        entered.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        HttpSignIn.PathAndQuery(entered.Headers.Location!).ShouldBe("/admin");

        var fleet = await client.GetAsync(new Uri("/admin/fleet", UriKind.Relative), Ct);
        fleet.StatusCode.ShouldBe(HttpStatusCode.OK, "the back office of the agency opens");
        var html = await fleet.Content.ReadAsStringAsync(Ct);
        html.ShouldContain(WebUtility.HtmlEncode(agency.Name), Case.Sensitive, "the name of the agency is in the app bar");
        html.ShouldContain("support-banner");
        await using (var scope = host.CreateScope())
        {
            (await scope.ServiceProvider.GetRequiredService<CarRentalDbContext>().Users.SingleAsync(u => u.Id == supportId, Ct)).SelectedAgencyId.ShouldBe(agency.Id);
        }

        var left = await PostAsync(client, "/admin/platform/leave", agencyId: null);
        left.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        HttpSignIn.PathAndQuery(left.Headers.Location!).ShouldBe("/admin/platform");
        (await RedirectOfAsync(client, "/admin/fleet")).ShouldBe("/admin/platform", "outside any agency again");
        await using (var scope = host.CreateScope())
        {
            (await scope.ServiceProvider.GetRequiredService<CarRentalDbContext>().Users.SingleAsync(u => u.Id == supportId, Ct)).SelectedAgencyId.ShouldBeNull();
        }
    }

    /// <summary>The target of the redirect answered for <paramref name="path"/>.</summary>
    private static async Task<string> RedirectOfAsync(HttpClient client, string path)
    {
        var response = await client.GetAsync(new Uri(path, UriKind.Relative), Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Redirect, path);
        return HttpSignIn.PathAndQuery(response.Headers.Location!);
    }

    /// <summary>A form post of the platform pages, with the antiforgery token of a back office page (the sign-out form).</summary>
    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string path, Guid? agencyId)
    {
        var page = await client.GetStringAsync(new Uri("/account/manage", UriKind.Relative), Ct);
        var fields = new Dictionary<string, string> { ["__RequestVerificationToken"] = HttpSignIn.AntiforgeryToken(page) };
        if (agencyId is { } id)
        {
            fields["agencyId"] = id.ToString("D");
        }

        using var form = new FormUrlEncodedContent(fields);
        return await client.PostAsync(new Uri(path, UriKind.Relative), form, Ct);
    }
}
