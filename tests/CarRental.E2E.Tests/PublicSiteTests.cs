using CarRental.E2E.Tests.Support;
using Deque.AxeCore.Playwright;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace CarRental.E2E.Tests;

/// <summary>Public home page on a 390 px wide screen, in French and English, without serious accessibility issue.</summary>
public sealed class PublicSiteTests(E2EHost host) : BrowserTest(host)
{
    [Theory]
    [InlineData("fr", "Louez votre voiture en Tunisie")]
    [InlineData("en", "Rent your car in Tunisia")]
    public async Task Home_page_renders_in_each_culture_on_a_phone(string culture, string heading)
    {
        var response = await Page.GotoAsync($"/{culture}");

        response.ShouldNotBeNull();
        response.Status.ShouldBe(200);
        await Expect(Page.Locator("html")).ToHaveAttributeAsync("lang", culture);
        await Expect(Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Level = 1 })).ToHaveTextAsync(heading);
        await Expect(Page.Locator("a.whatsapp__button")).ToBeVisibleAsync();

        // Mobile first: nothing overflows the 390 px viewport horizontally.
        var overflow = await Page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth > window.innerWidth");
        overflow.ShouldBeFalse();
    }

    [Theory]
    [InlineData("fr")]
    [InlineData("en")]
    public async Task Home_page_has_no_critical_or_serious_accessibility_violation(string culture)
    {
        await Page.GotoAsync($"/{culture}");

        await BackOfficeSession.WaitForTransitionsAsync(Page);

        var result = await Page.RunAxe();

        var blocking = result.Violations
            .Where(v => v.Impact is "critical" or "serious")
            .Select(v => $"{v.Id} ({v.Impact}): {v.Help} [{string.Join(", ", v.Nodes.Select(n => n.Target?.ToString()))}]")
            .ToList();
        blocking.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_language_switcher_keeps_the_page()
    {
        await Page.GotoAsync("/fr");

        await Page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = "English" }).ClickAsync();

        await Expect(Page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex("/en$"));
        await Expect(Page.Locator("html")).ToHaveAttributeAsync("lang", "en");
    }

    [Fact]
    public async Task The_skip_link_moves_the_focus_to_the_main_content()
    {
        await Page.GotoAsync("/fr");

        // Nothing is focused on load (no focus moved to the heading), so the skip link is the first tab stop.
        await Page.Keyboard.PressAsync("Tab");
        await Expect(Page.Locator("a.skip-link")).ToBeFocusedAsync();
        await Page.Keyboard.PressAsync("Enter");

        await Expect(Page.Locator("#main")).ToBeFocusedAsync();
    }
}
