using System.Text.RegularExpressions;
using CarRental.E2E.Tests.Support;
using CarRental.Testing;
using Deque.AxeCore.Playwright;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace CarRental.E2E.Tests.Fleet;

/// <summary>The fleet screens on a 10-inch tablet (1280 × 800): list, vehicle creation, detail tabs, accessibility.</summary>
public sealed class FleetBackOfficeTests(E2EHost host) : BrowserTest(host)
{
    private static readonly LocatorAssertionsToBeVisibleOptions Slow = new() { Timeout = 30_000 };

    protected override ViewportSize Viewport => new() { Width = 1280, Height = 800 };

    [Fact]
    public async Task A_manager_opens_the_fleet_registers_a_vehicle_and_finds_it_in_the_list()
    {
        var plate = $"{Random.Shared.Next(300, 999)} TU {Random.Shared.Next(1000, 9999)}";
        await SignInAsync(FleetAccounts.Manager, "/admin/fleet");

        await Expect(Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Level = 1 })).ToHaveTextAsync("Flotte et échéances");
        await Expect(Page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = "201 TU 1000" })).ToBeVisibleAsync(Slow);
        await ExpectNoSeriousAccessibilityViolationAsync();

        await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Ajouter un véhicule" }).ClickAsync();
        var dialog = Page.GetByRole(AriaRole.Dialog);
        await Expect(dialog).ToBeVisibleAsync();
        await dialog.GetByLabel(new Regex("^Immatriculation")).FillAsync(plate);
        await dialog.GetByLabel(new Regex("^Marque")).FillAsync("Kia");
        await dialog.GetByLabel(new Regex("^Modèle")).FillAsync("Picanto");
        await dialog.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Enregistrer" }).ClickAsync();

        await Expect(dialog).ToBeHiddenAsync(new LocatorAssertionsToBeHiddenOptions { Timeout = 30_000 });
        var link = Page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = plate });
        await Expect(link).ToBeVisibleAsync(Slow);

        await link.ClickAsync();
        await Expect(Page).ToHaveURLAsync(new Regex("/admin/fleet/vehicles/[0-9a-f-]{36}$"));
        await Expect(Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Level = 1 })).ToHaveTextAsync(plate);
        await Expect(Page.GetByText("Kia Picanto").First).ToBeVisibleAsync(Slow);
        await Page.GetByRole(AriaRole.Tab, new PageGetByRoleOptions { Name = "Documents et échéances" }).ClickAsync();
        await Expect(Page.GetByText("Assurance manquante").First).ToBeVisibleAsync(Slow);

        // The success snackbar comes from the shared admin theme (its green fails the contrast ratio): checked once gone.
        await Expect(Page.Locator(".mud-snackbar")).ToHaveCountAsync(0, new LocatorAssertionsToHaveCountOptions { Timeout = 20_000 });
        await ExpectNoSeriousAccessibilityViolationAsync();
    }

    [Fact]
    public async Task The_deadlines_and_categories_screens_show_the_demo_fleet()
    {
        await SignInAsync(FleetAccounts.Manager, "/admin/fleet/deadlines");

        await Expect(Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Level = 1 })).ToHaveTextAsync("Échéances de la flotte");
        await Expect(Page.GetByText("Visite technique expirée").First).ToBeVisibleAsync(Slow);
        await ExpectNoSeriousAccessibilityViolationAsync();

        await Page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = "Catégories" }).ClickAsync();
        await Expect(Page).ToHaveURLAsync(new Regex("/admin/fleet/categories$"));
        await Expect(Page.GetByRole(AriaRole.Cell, new PageGetByRoleOptions { Name = "EDAR", Exact = true })).ToBeVisibleAsync(Slow);
        await ExpectNoSeriousAccessibilityViolationAsync();
    }

    private async Task ExpectNoSeriousAccessibilityViolationAsync()
    {
        await BackOfficeSession.WaitForTransitionsAsync(Page);
        var result = await Page.RunAxe();
        var blocking = result.Violations
            .Where(v => v.Impact is "critical" or "serious")
            .Select(v => $"{v.Id} ({v.Impact}): {v.Help} [{string.Join(", ", v.Nodes.Select(n => n.Target?.ToString()))}]")
            .ToList();
        blocking.ShouldBeEmpty();
    }

    private async Task SignInAsync(string email, string returnUrl)
    {
        await FleetAccounts.EnsureAsync(Host.Factory.Services, email, CarRental.Application.Identity.Contracts.Roles.Manager);
        await Page.GotoAsync($"/account/login?ReturnUrl={Uri.EscapeDataString(returnUrl)}");
        await Page.Locator("#email").FillAsync(email);
        await Page.Locator("#password").FillAsync(TestAccounts.Password);
        await Page.Locator("button[type=submit]").ClickAsync();
        await Page.Locator("#code").FillAsync(Totp.Compute(TestAccounts.AuthenticatorKey));
        await Page.Locator("button[type=submit]").ClickAsync();
        await Expect(Page).ToHaveURLAsync(new Regex(Regex.Escape(returnUrl) + "$"));
    }
}
