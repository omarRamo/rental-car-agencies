using System.Text.RegularExpressions;
using CarRental.Application.Common.Security;
using CarRental.Application.Identity.Contracts;
using CarRental.Domain.Customers;
using CarRental.Domain.Identity;
using CarRental.Infrastructure.Identity;
using CarRental.Infrastructure.Persistence;
using CarRental.Testing;
using Deque.AxeCore.Playwright;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace CarRental.E2E.Tests.Customers;

/// <summary>
/// Customers back office on a 10-inch tablet (landscape): an agent finds a customer, opens the person and views an
/// identity document image through the secure viewer — the view is written to the document access log — without
/// serious accessibility issue.
/// </summary>
public sealed partial class CustomersBackOfficeTests(E2EHost host) : BrowserTest(host)
{
    private const string ReferencePersonEmail = "sami.trabelsi@example.com";

    protected override ViewportSize Viewport => new() { Width = 1280, Height = 800 };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_agent_searches_a_customer_opens_the_person_and_views_a_logged_document_image()
    {
        var agent = await NewStaffAsync(Roles.Agent);
        var documentId = await ReferenceNationalIdAsync();
        await SignInAsync(agent, "/admin/customers");
        await WaitForPersonsAsync();

        await Page.GetByLabel("Rechercher une personne").FillAsync("trabelsi");
        await Page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = "Sami Trabelsi" }).ClickAsync();

        await Expect(Page).ToHaveURLAsync(PersonUrl());
        await Expect(Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Level = 1 })).ToHaveTextAsync("Sami Trabelsi");
        var nationalId = Page.Locator("[data-document-type='NationalId']");
        await Expect(nationalId).ToContainTextAsync("08123456");

        await nationalId.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Afficher le recto" }).ClickAsync();
        var viewer = Page.GetByRole(AriaRole.Dialog);
        await Expect(viewer.Locator("img")).ToHaveCountAsync(0);
        await viewer.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Afficher", Exact = true }).ClickAsync();

        var image = viewer.Locator("img");
        await Expect(image).ToBeVisibleAsync();
        await Expect(image).ToHaveAttributeAsync("src", new Regex($"/admin/customers/documents/{documentId}/images/front\\?purpose=customer_file"));
        await Page.WaitForFunctionAsync("() => { const img = document.querySelector('[role=dialog] img'); return img && img.complete && img.naturalWidth > 0; }");

        var access = await WaitForAccessAsync(documentId, agent);
        access.Access.ShouldBe("view");
        access.Reason.ShouldBe("customer_file");
        access.DocumentKind.ShouldBe("identity_card.front");
    }

    [Fact]
    public async Task The_customers_screens_have_no_critical_or_serious_accessibility_violation()
    {
        await SignInAsync(await NewStaffAsync(Roles.Agent), "/admin/customers");
        await WaitForPersonsAsync();
        await Expect(Page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = "Sami Trabelsi" })).ToBeVisibleAsync();

        await AssertNoSeriousViolationAsync();

        await Page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = "Sami Trabelsi" }).ClickAsync();
        await Expect(Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Level = 1 })).ToHaveTextAsync("Sami Trabelsi");
        await Expect(Page.Locator("[data-document-type='NationalId']")).ToBeVisibleAsync();

        await AssertNoSeriousViolationAsync();

        // With an eligibility result, then with the secure viewer open.
        await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Vérifier l'éligibilité" }).ClickAsync();
        await Expect(Page.GetByRole(AriaRole.Status)).ToContainTextAsync("éligible");
        await AssertNoSeriousViolationAsync();

        await Page.Locator("[data-document-type='NationalId']").GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Afficher le recto" }).ClickAsync();
        await Expect(Page.GetByRole(AriaRole.Dialog)).ToBeVisibleAsync();
        await AssertNoSeriousViolationAsync();
    }

    [Fact]
    public async Task The_accountant_reads_customers_but_neither_document_numbers_nor_images()
    {
        await SignInAsync(await NewStaffAsync(Roles.Accountant), "/admin/customers");
        await WaitForPersonsAsync();

        await Page.GetByLabel("Rechercher une personne").FillAsync("trabelsi");
        await Page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = "Sami Trabelsi" }).ClickAsync();

        var nationalId = Page.Locator("[data-document-type='NationalId']");
        await Expect(nationalId).ToContainTextAsync("••••3456");
        await Expect(nationalId.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Afficher le recto" })).ToHaveCountAsync(0);
        await Expect(Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Ajouter une pièce" })).ToHaveCountAsync(0);
    }

    [GeneratedRegex("/admin/customers/persons/[0-9a-f-]{36}$")]
    private static partial Regex PersonUrl();

    /// <summary>The rows are loaded once the page is interactive: from then on, typing in the search box filters the list.</summary>
    private async Task WaitForPersonsAsync() =>
        await Expect(Page.Locator("table a[href^='/admin/customers/persons/']").First)
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30_000 });

    private async Task AssertNoSeriousViolationAsync()
    {
        var result = await Page.RunAxe();
        var blocking = result.Violations
            .Where(v => v.Impact is "critical" or "serious")
            .Select(v => $"{v.Id} ({v.Impact}): {v.Help} [{string.Join(", ", v.Nodes.Select(n => n.Html.Length > 300 ? n.Html[..300] : n.Html))}]")
            .ToList();
        blocking.ShouldBeEmpty();
    }

    private async Task SignInAsync(string email, string returnUrl)
    {
        await Page.GotoAsync($"/account/login?ReturnUrl={Uri.EscapeDataString(returnUrl)}");
        await Page.Locator("#email").FillAsync(email);
        await Page.Locator("#password").FillAsync(TestAccounts.Password);
        await Page.Locator("button[type=submit]").ClickAsync();
        await Page.Locator("#code").FillAsync(Totp.Compute(TestAccounts.AuthenticatorKey));
        await Page.Locator("button[type=submit]").ClickAsync();
        await Expect(Page).ToHaveURLAsync(new Regex(Regex.Escape(returnUrl) + "$"));
    }

    /// <summary>A staff account of its own (no concurrent sign-in updating the shared demo users).</summary>
    private async Task<string> NewStaffAsync(string role)
    {
        var email = $"{role.ToLowerInvariant()}-{Guid.NewGuid():N}@tests.local";
        await using var scope = Host.Factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ScopedUserContext>().Initialize(UserContextSnapshot.System("system:e2e"));
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = new ApplicationUser
        {
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            DisplayName = $"{role} E2E",
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

    private async Task<Guid> ReferenceNationalIdAsync()
    {
        await using var scope = Host.Factory.Services.CreateAsyncScope();
        var person = await scope.ServiceProvider.GetRequiredService<CarRentalDbContext>().Set<Person>()
            .AsNoTracking()
            .SingleAsync(p => p.Email == ReferencePersonEmail, Ct);
        return person.CurrentDocument(IdentityDocumentType.NationalId)!.Id;
    }

    private async Task<DocumentAccessLog> WaitForAccessAsync(Guid documentId, string userName)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            await using (var scope = Host.Factory.Services.CreateAsyncScope())
            {
                var access = await scope.ServiceProvider.GetRequiredService<CarRentalDbContext>().Set<DocumentAccessLog>()
                    .Where(a => a.DocumentId == documentId && a.UserName == userName)
                    .OrderByDescending(a => a.Id)
                    .FirstOrDefaultAsync(Ct);
                if (access is not null)
                {
                    return access;
                }
            }

            DateTime.UtcNow.ShouldBeLessThan(deadline, "the view of the image should be in the document access log");
            await Task.Delay(200, Ct);
        }
    }
}
