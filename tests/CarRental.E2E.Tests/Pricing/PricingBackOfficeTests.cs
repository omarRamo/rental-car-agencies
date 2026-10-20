using System.Text.RegularExpressions;
using CarRental.Testing;
using Deque.AxeCore.Playwright;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace CarRental.E2E.Tests.Pricing;

/// <summary>
/// Browser tests of the pricing screens. They run alone (not in parallel with other classes): two parallel sign-ins of the
/// same demo account can make the server fail the second-factor step of one of them.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PricingBrowserCollection
{
    public const string Name = "Pricing browser tests";
}

/// <summary>Pricing screens on a 10-inch tablet in landscape: the simulator reproduces SPEC §7, the plan screens are accessible.</summary>
[Collection(PricingBrowserCollection.Name)]
public sealed class PricingBackOfficeTests(E2EHost host) : BrowserTest(host)
{
    protected override ViewportSize Viewport => new() { Width = 1280, Height = 800 };

    [Fact]
    public async Task Manager_runs_the_reference_example_in_the_simulator()
    {
        await SignInAsync(TestAccounts.ManagerEmail, "/admin/pricing/simulator?cat=ECMR");
        await Expect(Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Level = 1 })).ToHaveTextAsync("Simulateur de prix");
        await WaitUntilReadyAsync();
        var calculate = Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Calculer" });
        await Expect(calculate).ToBeEnabledAsync();

        // SPEC §7: ECMR, 28/06/2027 10:00 → 03/07/2027 10:00, one additional driver, one child seat, booked the day before.
        await Page.GetByLabel("Départ", new PageGetByLabelOptions { Exact = true }).FillAsync("2027-06-28T10:00");
        await Page.GetByLabel("Retour", new PageGetByLabelOptions { Exact = true }).FillAsync("2027-07-03T10:00");
        await Page.GetByLabel("Date de la demande", new PageGetByLabelOptions { Exact = true }).FillAsync("2027-06-27T10:00");
        await Page.GetByLabel("Conducteur additionnel", new PageGetByLabelOptions { Exact = true }).FillAsync("1");
        await Page.GetByLabel("Siège enfant", new PageGetByLabelOptions { Exact = true }).FillAsync("1");
        await calculate.ClickAsync();

        await Expect(Page.GetByTestId("quote-total")).ToHaveTextAsync("681,400 TND", new LocatorAssertionsToHaveTextOptions { Timeout = 30_000 });
        var lines = Page.Locator(".quote-lines table").First;
        await Expect(lines.GetByRole(AriaRole.Rowheader, new LocatorGetByRoleOptions { Name = "Jours en moyenne saison" })).ToBeVisibleAsync();
        await Expect(lines.GetByRole(AriaRole.Rowheader, new LocatorGetByRoleOptions { Name = "Jours en haute saison" })).ToBeVisibleAsync();
        await Expect(lines.GetByRole(AriaRole.Rowheader, new LocatorGetByRoleOptions { Name = "Remise palier 3–6 jours −5 %" })).ToBeVisibleAsync();
        await Expect(lines.Locator("tr", new LocatorLocatorOptions { HasText = "Contribution journalière" })).ToContainTextAsync("11,900 TND");
        await Expect(lines.Locator("tfoot")).ToContainTextAsync("572,605 TND");
        await Expect(lines.Locator("tfoot")).ToContainTextAsync("108,795 TND");
        await Expect(Page.Locator("dl.quote-summary")).ToContainTextAsync("600,000 TND");

        await AssertAccessibleAsync();
    }

    [Fact]
    public async Task Accountant_reads_the_published_demo_plan_without_editing_it()
    {
        // The accountant holds pricing.read only: same screens, no editing action.
        await SignInAsync(TestAccounts.AccountantEmail, "/admin/pricing");
        await Expect(Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Level = 1 })).ToHaveTextAsync("Tarifs et saisons");
        await WaitUntilReadyAsync();
        // Other tests may add drafts copied from the demo plan (same name): target the published version.
        var row = Page.Locator("tr", new PageLocatorOptions { HasText = "Grille démo 2026-2028" })
            .Filter(new LocatorFilterOptions { HasText = "Publiée" });
        await Expect(row).ToContainTextAsync("Publiée", new LocatorAssertionsToContainTextOptions { Timeout = 30_000 });
        await Expect(row).ToContainTextAsync("v1");
        await AssertAccessibleAsync();

        await row.GetByRole(AriaRole.Link, new LocatorGetByRoleOptions { Name = "Grille démo 2026-2028", Exact = true }).ClickAsync();

        await Expect(Page).ToHaveURLAsync(new Regex("/admin/pricing/plans/[0-9a-f-]{36}$"));
        await Expect(Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Level = 1 })).ToHaveTextAsync("Grille démo 2026-2028");
        await WaitUntilReadyAsync();
        await Expect(Page.GetByText("Grille publiée : elle ne se modifie plus.", new PageGetByTextOptions { Exact = false })).ToBeVisibleAsync();
        await Expect(Page.Locator("table.season-month").First).ToBeVisibleAsync();
        await Expect(Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Créer un brouillon" })).ToHaveCountAsync(0);
        await Expect(Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Archiver" })).ToHaveCountAsync(0);
        await AssertAccessibleAsync();

        await Page.GetByRole(AriaRole.Tab, new PageGetByRoleOptions { Name = "Grille tarifaire" }).ClickAsync();
        await Expect(Page.GetByRole(AriaRole.Rowheader, new PageGetByRoleOptions { Name = "ECMR — Économique" })).ToBeVisibleAsync();
        await AssertAccessibleAsync();
    }

    [Fact]
    public async Task Manager_creates_edits_and_publishes_an_own_grid_for_the_counter()
    {
        await SignInAsync(TestAccounts.ManagerEmail, "/admin/pricing");
        await WaitUntilReadyAsync();

        await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Nouveau brouillon" }).ClickAsync();
        var dialog = Page.GetByRole(AriaRole.Dialog);
        await dialog.Locator(".mud-select").First.ClickAsync();
        await Page.Locator(".mud-popover-open").GetByText("Comptoir", new LocatorGetByTextOptions { Exact = true }).ClickAsync();
        await dialog.GetByLabel("Nom du brouillon").FillAsync("Grille comptoir");
        await dialog.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Créer le brouillon" }).ClickAsync();

        await Expect(Page).ToHaveURLAsync(new Regex("/admin/pricing/plans/[0-9a-f-]{36}$"));
        await Expect(Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Level = 1 })).ToHaveTextAsync("Grille comptoir");
        await WaitUntilReadyAsync();
        await Page.GetByRole(AriaRole.Tab, new PageGetByRoleOptions { Name = "Grille tarifaire" }).ClickAsync();
        var mid = Page.GetByLabel("Prix TTC par jour, ECMR — Économique, Moyenne saison", new PageGetByLabelOptions { Exact = true });
        await Expect(mid).ToHaveValueAsync("110,000");
        await mid.FillAsync("115");
        await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Enregistrer la grille" }).ClickAsync();
        await Expect(Page.GetByText("Modifications enregistrées.")).ToBeVisibleAsync();
        await AssertAccessibleAsync();

        await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Publier" }).ClickAsync();
        await Page.GetByRole(AriaRole.Dialog).GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Publier" }).ClickAsync();

        await Expect(Page.GetByText("Grille publiée : elle ne se modifie plus.", new PageGetByTextOptions { Exact = false })).ToBeVisibleAsync();
        await Expect(Page.Locator("h1 + div")).ToContainTextAsync("v1");
        // The field keeps the typed text when the saved value is the same number (115 = 115.000).
        await Expect(Page.GetByLabel("Prix TTC par jour, ECMR — Économique, Moyenne saison", new PageGetByLabelOptions { Exact = true }))
            .ToHaveValueAsync(new Regex("^115(,000)?$"));
        await Expect(Page.GetByLabel("Prix TTC par jour, ECMR — Économique, Moyenne saison", new PageGetByLabelOptions { Exact = true }))
            .ToBeDisabledAsync();
    }

    /// <summary>Waits for the interactive render of the page (its h1 carries data-ready once the circuit has loaded it).</summary>
    private Task WaitUntilReadyAsync() =>
        Expect(Page.Locator("h1[data-ready=true]")).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30_000 });

    private async Task AssertAccessibleAsync()
    {
        // Toasts fade in and out (transient blended colours): close them, the page content is what is checked.
        foreach (var close in await Page.Locator(".mud-snackbar button").AllAsync())
        {
            await close.ClickAsync();
        }

        await Expect(Page.Locator(".mud-snackbar")).ToHaveCountAsync(0);
        var result = await Page.RunAxe();
        var blocking = result.Violations
            .Where(v => v.Impact is "critical" or "serious")
            .Select(v => $"{v.Id} ({v.Impact}): {v.Help} [{string.Join(", ", v.Nodes.Select(n => $"{n.Target} {n.Html} {string.Join(" ", n.Any.Select(c => c.Message))}"))}]")
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
}
