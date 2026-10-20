using CarRental.Application.Identity.Contracts;
using CarRental.E2E.Tests.Support;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace CarRental.E2E.Tests;

/// <summary>
/// The back office palette and components meet WCAG 2.2 AA: every colour on buttons, chips and alerts, the usual input
/// fields and a paged table of <c>/admin/ui-gallery</c> pass axe (no critical or serious violation), on a phone and on
/// a 10-inch tablet; MudBlazor's own texts follow the interface language.
/// </summary>
public sealed class AccessibilityGalleryTests(E2EHost host) : BrowserTest(host)
{
    private const string GalleryUrl = "/admin/ui-gallery";

    [Fact]
    public async Task Every_colour_component_and_the_paged_table_pass_axe_on_a_phone()
    {
        await OpenGalleryAsync();

        await BackOfficeSession.AssertNoSeriousAxeViolationAsync(Page);
    }

    [Fact]
    public async Task The_pager_has_a_labelled_page_size_and_french_navigation_buttons()
    {
        await Page.SetViewportSizeAsync(1280, 800);
        await OpenGalleryAsync();
        var table = Page.Locator("section[aria-labelledby='gallery-table']");
        var pageSize = table.GetByRole(AriaRole.Combobox, new LocatorGetByRoleOptions { Name = "Lignes par page" });
        await Expect(pageSize).ToHaveTextAsync("10");
        await Expect(table.GetByText("1–10 sur 60")).ToBeVisibleAsync();

        await table.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Page suivante" }).ClickAsync();
        await Expect(table.GetByText("11–20 sur 60")).ToBeVisibleAsync();
        await Expect(table.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Dernière page" })).ToBeEnabledAsync();

        await pageSize.ClickAsync();
        await Page.GetByRole(AriaRole.Option, new PageGetByRoleOptions { Name = "25", Exact = true }).ClickAsync();
        await Expect(table.GetByText("1–25 sur 60")).ToBeVisibleAsync();
        await BackOfficeSession.AssertNoSeriousAxeViolationAsync(Page);
    }

    [Fact]
    public async Task An_open_date_picker_passes_axe_and_speaks_french()
    {
        await Page.SetViewportSizeAsync(1280, 800);
        await OpenGalleryAsync();

        await Page.GetByLabel("Date", new PageGetByLabelOptions { Exact = true }).ClickAsync();
        await Expect(Page.Locator(".mud-picker-content")).ToBeVisibleAsync();
        await BackOfficeSession.WaitForOpenPopoverAsync(Page);
        await Expect(Page.Locator("button[aria-label^='Mois précédent']")).ToBeVisibleAsync();

        await BackOfficeSession.AssertNoSeriousAxeViolationAsync(Page);
    }

    [Fact]
    public async Task An_open_time_picker_passes_axe()
    {
        await Page.SetViewportSizeAsync(1280, 800);
        await OpenGalleryAsync();

        await Page.GetByLabel("Heure", new PageGetByLabelOptions { Exact = true }).ClickAsync();
        await Expect(Page.Locator(".mud-picker-content")).ToBeVisibleAsync();
        await BackOfficeSession.WaitForOpenPopoverAsync(Page);

        await BackOfficeSession.AssertNoSeriousAxeViolationAsync(Page);
    }

    [Fact]
    public async Task MudBlazor_texts_switch_to_english_with_the_interface()
    {
        await Page.SetViewportSizeAsync(1280, 800);
        await OpenGalleryAsync();

        await Page.GotoAsync($"/culture/set?culture=en&returnUrl={Uri.EscapeDataString(GalleryUrl)}");
        await BackOfficeSession.WaitUntilInteractiveAsync(Page);
        await Expect(Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Level = 1 })).ToHaveTextAsync("Accessibility gallery");
        var table = Page.Locator("section[aria-labelledby='gallery-table']");
        await Expect(table.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Next page" })).ToBeVisibleAsync();
        await Expect(table.GetByRole(AriaRole.Combobox, new LocatorGetByRoleOptions { Name = "Rows per page" })).ToBeVisibleAsync();
    }

    private async Task OpenGalleryAsync()
    {
        await BackOfficeSession.SignInAsync(Page, await BackOfficeSession.NewStaffAsync(Host, Roles.Manager), GalleryUrl);
        await BackOfficeSession.WaitUntilInteractiveAsync(Page);
        await Expect(Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Level = 1 })).ToHaveTextAsync("Galerie d'accessibilité");
    }
}
