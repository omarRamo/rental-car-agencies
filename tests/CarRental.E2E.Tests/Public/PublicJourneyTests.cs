using System.Globalization;
using System.Text.RegularExpressions;
using CarRental.Domain.Common;
using CarRental.E2E.Tests.Support;
using Deque.AxeCore.Playwright;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace CarRental.E2E.Tests.Public;

/// <summary>
/// The public journey on a 390 px wide phone: home → search → results in French and English, filters, a WhatsApp deep
/// link, keyboard-only use, accessibility (axe) and no horizontal scroll on every public page.
/// </summary>
public sealed class PublicJourneyTests(E2EHost host) : BrowserTest(host)
{
    /// <summary>Tunis wall-clock date and time <paramref name="days"/> days from today, as in a deep link.</summary>
    private static string LocalDate(int days) =>
        TunisTime.Today(TimeProvider.System).AddDays(days).ToDateTime(new TimeOnly(10, 0)).ToString("yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture);

    private async Task ShouldNotScrollHorizontallyAsync()
    {
        var overflow = await Page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth > window.innerWidth");
        overflow.ShouldBeFalse($"{Page.Url} scrolls horizontally at 390 px.");
    }

    private async Task ShouldHaveNoSeriousAccessibilityViolationAsync()
    {
        await BackOfficeSession.WaitForTransitionsAsync(Page);
        var result = await Page.RunAxe();
        var blocking = result.Violations
            .Where(v => v.Impact is "critical" or "serious")
            .Select(v => $"{v.Id} ({v.Impact}): {v.Help} [{string.Join(", ", v.Nodes.Select(n => n.Target?.ToString()))}]")
            .ToList();
        blocking.ShouldBeEmpty($"{Page.Url}");
    }

    [Theory]
    [InlineData("fr")]
    [InlineData("en")]
    public async Task A_visitor_searches_from_the_home_page_and_sees_the_total_prices(string culture)
    {
        var (locationLabel, submit, resultsPath, heading, totalLabel) = culture == "fr"
            ? ("Lieu de départ", "Rechercher", "/fr/reserver", "Voitures disponibles", "Total TTC")
            : ("Pick-up location", "Search", "/en/book", "Available cars", "Total incl. VAT");
        await Page.GotoAsync($"/{culture}");

        await Page.GetByLabel(locationLabel).SelectOptionAsync("TUN");
        await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = submit, Exact = true }).ClickAsync();

        // A plain GET form: the empty "same location" return is sent too.
        await Expect(Page).ToHaveURLAsync(new Regex($"{Regex.Escape(resultsPath)}\\?lieu=TUN&retour_lieu=&depart="));
        await Expect(Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Level = 1 })).ToHaveTextAsync(heading);
        var offers = Page.Locator("li.offer");
        (await offers.CountAsync()).ShouldBeGreaterThan(0);
        await Expect(offers.First.Locator(".offer__total-label")).ToHaveTextAsync(totalLabel);
        await Expect(offers.First.Locator(".offer__amount")).ToContainTextAsync("TND");
        await Expect(offers.First.Locator(".offer__eur")).ToContainTextAsync("€");
        await ShouldNotScrollHorizontallyAsync();
    }

    [Fact]
    public async Task Filters_and_sort_are_applied_with_a_plain_form()
    {
        await Page.GotoAsync($"/fr/reserver?lieu=TUN&depart={LocalDate(15)}&retour={LocalDate(18)}");

        await Page.GetByLabel("Boîte de vitesses").SelectOptionAsync("auto");
        await Page.GetByLabel("Trier par").SelectOptionAsync("prix_desc");
        await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Appliquer" }).ClickAsync();

        await Expect(Page).ToHaveURLAsync(new Regex("boite=auto&places=&tri=prix_desc|boite=auto.*tri=prix_desc"));
        var codes = await Page.Locator("li.offer .offer__code").AllTextContentsAsync();
        codes.ShouldBe(["EDAR"]);
        await Expect(Page.GetByLabel("Boîte de vitesses")).ToHaveValueAsync("auto");
    }

    [Fact]
    public async Task A_whatsapp_deep_link_shows_the_chosen_category_first()
    {
        await Page.GotoAsync($"/fr/reserver?lieu=TUN&depart={LocalDate(16)}&retour={LocalDate(20)}&cat=ECMR&boite=manuelle&places=5&devis=Q7F3K2&src=whatsapp");

        var first = Page.Locator("li.offer").First;
        await Expect(first).ToHaveClassAsync(new Regex("offer--highlighted"));
        await Expect(first.Locator(".offer__code")).ToHaveTextAsync("ECMR");
        await Expect(first.Locator(".offer__badge")).ToHaveTextAsync("Votre sélection");
        await Expect(Page.GetByText("Devis Q7F3K2")).ToBeVisibleAsync();
        await Expect(Page.Locator("form.offer__choose input[name='src']").First).ToHaveValueAsync("whatsapp");
        await ShouldNotScrollHorizontallyAsync();
    }

    [Fact]
    public async Task The_whole_search_can_be_done_with_the_keyboard()
    {
        await Page.GotoAsync("/fr");

        // From the top of the page to the search button, then to the first "Choose" button of the results.
        await TabUntilFocusedAsync("#search-lieu");
        await Page.Keyboard.PressAsync("ArrowDown");
        await TabUntilFocusedAsync("form.search-form button[type=submit]");
        await Page.Keyboard.PressAsync("Enter");
        await Expect(Page).ToHaveURLAsync(new Regex("/fr/reserver\\?"));

        await TabUntilFocusedAsync("form.offer__choose button");
        await Page.Keyboard.PressAsync("Enter");

        // The booking funnel opens on its first step (package 2C).
        await Expect(Page).ToHaveURLAsync(new Regex("/fr/reserver/[A-Za-z0-9_-]{43}/options$"));
        await Expect(Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Level = 1 })).ToHaveTextAsync("Options et prix");
    }

    [Fact]
    public async Task An_invalid_deep_link_focuses_the_error_summary_whose_links_lead_to_the_fields()
    {
        await Page.GotoAsync("/fr/reserver?lieu=TUN&depart=2020-07-01T10:00&retour=2020-07-03T10:00");

        await Expect(Page).ToHaveTitleAsync(new Regex("^Erreur : "));
        var summary = Page.Locator(".error-summary");
        await Expect(summary).ToBeFocusedAsync();
        await Page.Keyboard.PressAsync("Tab");
        await Page.Keyboard.PressAsync("Enter");

        await Expect(Page.Locator("#search-depart")).ToBeFocusedAsync();
        await Expect(Page.Locator("#search-depart")).ToHaveAttributeAsync("aria-invalid", "true");
        await ShouldHaveNoSeriousAccessibilityViolationAsync();
    }

    [Fact]
    public async Task Returning_elsewhere_reveals_the_return_location()
    {
        await Page.GotoAsync("/en");
        var returnLocation = Page.GetByLabel("Return location");

        await Expect(returnLocation).ToBeHiddenAsync();
        await Page.GetByLabel("Return the car to another location").CheckAsync();
        await Expect(returnLocation).ToBeVisibleAsync();
        await returnLocation.SelectOptionAsync("NBE");
        await Page.GetByLabel("Return the car to another location").UncheckAsync();

        await Expect(returnLocation).ToBeHiddenAsync();
        await Expect(returnLocation).ToHaveValueAsync(string.Empty);
    }

    private static readonly Dictionary<string, string> Pages = new(StringComparer.Ordinal)
    {
        ["home-fr"] = "/fr",
        ["home-en"] = "/en",
        ["results-fr"] = "/fr/reserver?lieu=TUN&depart={0}&retour={1}",
        ["results-en"] = "/en/book?lieu=NBE&retour_lieu=TUN&depart={0}&retour={1}&cat=EDAR",
        ["agency-fr"] = "/fr/agence",
        ["agency-en"] = "/en/agency",
        ["faq-fr"] = "/fr/faq",
        ["terms-fr"] = "/fr/conditions",
        ["privacy-fr"] = "/fr/confidentialite",
        ["privacy-en"] = "/en/privacy",
    };

    [Theory]
    [InlineData("home-fr")]
    [InlineData("home-en")]
    [InlineData("results-fr")]
    [InlineData("results-en")]
    [InlineData("agency-fr")]
    [InlineData("agency-en")]
    [InlineData("faq-fr")]
    [InlineData("terms-fr")]
    [InlineData("privacy-fr")]
    [InlineData("privacy-en")]
    public async Task Public_pages_pass_axe_and_fit_a_phone(string page)
    {
        var response = await Page.GotoAsync(string.Format(CultureInfo.InvariantCulture, Pages[page], LocalDate(17), LocalDate(21)));

        response!.Status.ShouldBe(200);
        await Expect(Page.Locator("h1")).ToHaveCountAsync(1);
        await ShouldHaveNoSeriousAccessibilityViolationAsync();
        await ShouldNotScrollHorizontallyAsync();
    }

    [Fact]
    public async Task The_language_switcher_keeps_the_search()
    {
        await Page.GotoAsync($"/fr/reserver?lieu=TUN&depart={LocalDate(18)}&retour={LocalDate(22)}&cat=CDMR");

        await Page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = "English" }).ClickAsync();

        await Expect(Page).ToHaveURLAsync(new Regex($"/en/book\\?lieu=TUN&depart={Regex.Escape(LocalDate(18))}.*&cat=CDMR"));
        await Expect(Page.Locator("html")).ToHaveAttributeAsync("lang", "en");
        await Expect(Page.Locator("li.offer").First.Locator(".offer__code")).ToHaveTextAsync("CDMR");
    }

    [Fact]
    public async Task The_skip_link_stays_on_the_page_beyond_the_home_page()
    {
        await Page.GotoAsync("/fr/agence");

        await Page.Keyboard.PressAsync("Tab");
        await Expect(Page.Locator("a.skip-link")).ToBeFocusedAsync();
        await Page.Keyboard.PressAsync("Enter");

        await Expect(Page).ToHaveURLAsync(new Regex("/fr/agence#main$"));
        await Expect(Page.Locator("#main")).ToBeFocusedAsync();
    }

    /// <summary>Presses Tab until <paramref name="selector"/> has the focus (at most 60 times).</summary>
    private async Task TabUntilFocusedAsync(string selector)
    {
        for (var press = 0; press < 60; press++)
        {
            await Page.Keyboard.PressAsync("Tab");
            if (await Page.EvaluateAsync<bool>("selector => document.activeElement?.matches(selector) ?? false", selector))
            {
                return;
            }
        }

        throw new ShouldAssertException($"{selector} was never reached with the Tab key on {Page.Url}.");
    }
}
