using CarRental.Testing;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace CarRental.E2E.Tests;

/// <summary>Phase 0 acceptance: a manager signs in with two-factor authentication and sees their actions in the audit log.</summary>
public sealed class BackOfficeTests(E2EHost host) : BrowserTest(host)
{
    protected override ViewportSize Viewport => new() { Width = 1366, Height = 900 };

    [Fact]
    public async Task Manager_signs_in_with_totp_and_finds_the_login_in_the_audit_log()
    {
        await Page.GotoAsync("/account/login?ReturnUrl=%2Fadmin%2Faudit");
        await Page.Locator("#email").FillAsync(TestAccounts.ManagerEmail);
        await Page.Locator("#password").FillAsync(TestAccounts.Password);
        await Page.Locator("button[type=submit]").ClickAsync();

        await Expect(Page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex("/account/login-with-2fa"));
        await Page.Locator("#code").FillAsync(Totp.Compute(TestAccounts.AuthenticatorKey));
        await Page.Locator("button[type=submit]").ClickAsync();

        await Expect(Page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex("/admin/audit$"));
        await Expect(Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Level = 1 })).ToHaveTextAsync("Journal d'audit");

        var loginRow = Page.Locator("tr", new PageLocatorOptions { HasText = "identity.login_succeeded" })
            .Filter(new LocatorFilterOptions { HasText = TestAccounts.ManagerEmail })
            .First;
        await Expect(loginRow).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30_000 });
    }

    [Fact]
    public async Task The_back_office_menu_shows_the_screens_of_the_manager()
    {
        await SignInAsync(TestAccounts.ManagerEmail, "/admin");

        await Expect(Page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = "Journal d'audit" })).ToBeVisibleAsync();
        await Expect(Page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = "Utilisateurs et rôles" })).ToBeVisibleAsync();
        await Page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = "Flotte et échéances" }).ClickAsync();

        await Expect(Page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex("/admin/fleet$"));
        await Expect(Page.GetByText("À venir")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task The_skip_link_and_in_app_navigation_move_the_keyboard_focus()
    {
        await SignInAsync(TestAccounts.ManagerEmail, "/admin/audit");
        // The table loads its rows once interactive: from here on, the interactive router handles the links.
        await Expect(Page.Locator("tr", new PageLocatorOptions { HasText = "identity.login_succeeded" }).First)
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30_000 });

        // Nothing is focused on load, so the skip link is the first tab stop; it moves the focus to the main content.
        await Page.Keyboard.PressAsync("Tab");
        await Expect(Page.Locator("a.skip-link")).ToBeFocusedAsync();
        await Page.Keyboard.PressAsync("Enter");
        await Expect(Page.Locator("#admin-main")).ToBeFocusedAsync();

        // After an in-app navigation, the focus moves to the heading of the new page.
        await Page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = "Utilisateurs et rôles" }).ClickAsync();
        await Expect(Page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex("/admin/users$"));
        var heading = Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Level = 1 });
        await Expect(heading).ToHaveTextAsync("Utilisateurs");
        await Expect(heading).ToBeFocusedAsync();
    }

    private async Task SignInAsync(string email, string returnUrl)
    {
        await Page.GotoAsync($"/account/login?ReturnUrl={Uri.EscapeDataString(returnUrl)}");
        await Page.Locator("#email").FillAsync(email);
        await Page.Locator("#password").FillAsync(TestAccounts.Password);
        await Page.Locator("button[type=submit]").ClickAsync();
        await Page.Locator("#code").FillAsync(Totp.Compute(TestAccounts.AuthenticatorKey));
        await Page.Locator("button[type=submit]").ClickAsync();
        await Expect(Page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex(System.Text.RegularExpressions.Regex.Escape(returnUrl) + "$"));
    }
}
