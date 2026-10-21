using System.Text.RegularExpressions;
using CarRental.Application.Identity.Contracts;
using CarRental.Domain.Bookings;
using CarRental.Domain.Bookings.Handover;
using CarRental.E2E.Tests.Support;
using Deque.AxeCore.Playwright;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace CarRental.E2E.Tests.Bookings.Handover;

/// <summary>
/// The departure and return screens on a 10-inch tablet (1280 × 800): the eight photos taken (real images reduced by
/// the browser), a damage tapped on the schematic, the contract read and signed with the mouse, the keys handed over
/// and the contract opened; a return with a new damage, a charge, the deposit retention and the closing. Each test
/// signs in with its own account and works on its own agency; no serious or critical accessibility violation.
/// </summary>
public sealed class HandoverBackOfficeTests(E2EHost host) : BrowserTest(host)
{
    private static readonly LocatorAssertionsToBeVisibleOptions Slow = new() { Timeout = 30_000 };

    private readonly List<string> _console = [];

    protected override ViewportSize Viewport => new() { Width = 1280, Height = 800 };

    [Fact]
    public async Task An_agent_hands_a_vehicle_over_with_the_photos_a_drawn_signature_and_the_contract()
    {
        WatchConsole();
        var file = await HandoverE2EData.ConfirmedAsync(Host);
        var email = await BackOfficeSession.NewStaffAsync(Host, Roles.Agent);
        await BackOfficeSession.SignInAsync(Page, email, $"/admin/handovers/{file.BookingId}/departure");
        await Expect(Page.Locator("h1[data-ready=true]")).ToHaveTextAsync($"Départ — {file.Number}", new LocatorAssertionsToHaveTextOptions { Timeout = 30_000 });

        // Originals: the first step left to do; each box is saved at once.
        await Expect(Page.Locator("[data-step-panel=originals]")).ToBeVisibleAsync(Slow);
        await AssertAccessibleAsync();
        await Page.GetByText("Pièce d'identité originale vue").ClickAsync();
        await Expect(Page.GetByLabel("Pièce d'identité originale vue")).ToBeCheckedAsync();
        await Page.GetByText("Permis de conduire original vu").ClickAsync();
        await Expect(Step("originals")).ToHaveClassAsync(new Regex("handover-step--done"));
        await NextAsync("vehicle");

        await Page.Locator("[data-testid=vehicle-confirm]").ClickAsync();
        await Expect(Page.Locator("[data-step-panel=photos]")).ToBeVisibleAsync(Slow);

        // The eight guided views from the "camera": real images, reduced in the browser before the upload.
        foreach (var slot in PhotoSlots.Guided)
        {
            await Page.Locator($"[data-testid=photo-input-{slot}]").SetInputFilesAsync(new FilePayload
            {
                Name = $"{slot}.png",
                MimeType = "image/png",
                Buffer = HandoverE2EData.Png(2400, 1800, (int)slot),
            });
            await Expect(Page.Locator($"div[data-slot={slot}][data-has-photo=true]")).ToBeVisibleAsync(Slow);
        }

        await Expect(Page.Locator("[data-testid=photos-missing]")).ToHaveTextAsync("Toutes les vues guidées sont prises.");
        await AssertAccessibleAsync();
        await NextAsync("readings");

        await Page.GetByLabel("Kilométrage", new PageGetByLabelOptions { Exact = true }).FillAsync("15120");
        await Page.Locator("label:has([data-testid=fuel-8])").ClickAsync();
        await Page.Locator("[data-testid=readings-save]").ClickAsync();
        await Expect(Page.Locator("[data-step-panel=damages]")).ToBeVisibleAsync(Slow);

        // A scratch tapped on the left side of the schematic.
        await Page.Locator("[data-testid=schematic-Departure] svg[data-view=Left]").ClickAsync(new LocatorClickOptions { Position = new Position { X = 60, Y = 30 } });
        var dialog = Page.GetByRole(AriaRole.Dialog);
        await Expect(dialog).ToBeVisibleAsync();
        await BackOfficeSession.WaitForOpenDialogAsync(Page);
        await AssertAccessibleAsync();
        await dialog.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Ajouter", Exact = true }).ClickAsync();
        await Expect(Page.Locator("li[data-damage='1']")).ToBeVisibleAsync(Slow);
        await Expect(Page.Locator("[data-testid=schematic-Departure] g[data-damage='1']")).ToBeVisibleAsync();

        // The money is taken at the counter (payments panel): the screen is resumed afterwards, as on another tablet.
        await HandoverE2EData.SettleAsync(Host, file);
        await Page.GotoAsync($"/admin/handovers/{file.BookingId}/departure?step=payment");
        await Expect(Page.Locator("[data-step-panel=payment]")).ToBeVisibleAsync(Slow);
        await Expect(Step("payment")).ToHaveClassAsync(new Regex("handover-step--done"));
        await Expect(Page.Locator("[data-testid=booking-payments][data-ready=true]")).ToBeVisibleAsync(Slow);
        await AssertAccessibleAsync();
        await NextAsync("contract");

        await Expect(Page.Locator("[data-testid=contract-preview]")).ToContainTextAsync(file.Number, new LocatorAssertionsToContainTextOptions { Timeout = 30_000 });
        await SignAsync("Customer");
        await SignAsync("Agent");
        await Expect(Step("contract")).ToHaveClassAsync(new Regex("handover-step--done"));
        await AssertAccessibleAsync();
        await NextAsync("finish");

        await Expect(Page.Locator("[data-testid=finish-issues]")).ToHaveCountAsync(0);
        await AssertAccessibleAsync();
        await Page.Locator("[data-testid=departure-finish]").ClickAsync();
        await Expect(Page.Locator("[data-testid=departure-done]")).ToBeVisibleAsync(Slow);
        (await HandoverE2EData.StatusAsync(Host, file.BookingId)).ShouldBe(BookingStatus.InProgress);

        // The contract opens as a PDF for the signed-in agent.
        var contract = Page.Locator("[data-testid=contract-open]");
        await Expect(contract).ToContainTextAsync($"{file.Number}-v1.pdf");
        var response = await Page.Context.APIRequest.GetAsync((await contract.GetAttributeAsync("href")).ShouldNotBeNull());
        response.Status.ShouldBe(200);
        response.Headers["content-type"].ShouldStartWith("application/pdf");
        (await response.BodyAsync()).Length.ShouldBeGreaterThan(1_000);
        await AssertAccessibleAsync();
    }

    [Fact]
    public async Task An_agent_takes_a_vehicle_back_with_a_new_damage_retains_the_charge_and_closes_the_file()
    {
        WatchConsole();
        var file = await HandoverE2EData.ConfirmedAsync(Host);
        await HandoverE2EData.DepartAsync(Host, file);
        var email = await BackOfficeSession.NewStaffAsync(Host, Roles.Agent);
        await BackOfficeSession.SignInAsync(Page, email, $"/admin/handovers/{file.BookingId}/return");
        await Expect(Page.Locator("h1[data-ready=true]")).ToHaveTextAsync($"Retour — {file.Number}", new LocatorAssertionsToHaveTextOptions { Timeout = 30_000 });

        await Expect(Page.Locator("[data-step-panel=readings]")).ToBeVisibleAsync(Slow);
        await AssertAccessibleAsync();
        await Page.GetByLabel("Kilométrage", new PageGetByLabelOptions { Exact = true }).FillAsync("15460");
        await Page.Locator("label:has([data-testid=fuel-8])").ClickAsync();
        await Page.Locator("[data-testid=readings-save]").ClickAsync();
        await Expect(Page.Locator("[data-step-panel=photos]")).ToBeVisibleAsync(Slow);

        await Page.Locator("[data-testid=photo-input-Front]").SetInputFilesAsync(new FilePayload { Name = "front.png", MimeType = "image/png", Buffer = HandoverE2EData.Png(1600, 1200, 3) });
        await Expect(Page.Locator("div[data-slot=Front][data-has-photo=true]")).ToBeVisibleAsync(Slow);
        await Expect(Page.GetByAltText("Photo au départ : Avant")).ToBeVisibleAsync();
        await NextAsync("damages");

        // The keyboard path: "add a damage" opens the same dialog as a tap on the schematic.
        await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Ajouter un dommage" }).ClickAsync();
        var dialog = Page.GetByRole(AriaRole.Dialog);
        await Expect(dialog).ToBeVisibleAsync();
        await dialog.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Ajouter", Exact = true }).ClickAsync();
        await Expect(Page.Locator("li[data-damage][data-new=true]")).ToBeVisibleAsync(Slow);
        await Expect(Page.Locator("[data-testid=new-damages]")).ToContainTextAsync("1 nouveau");
        await AssertAccessibleAsync();
        await NextAsync("charges");

        await Expect(Page.Locator("[data-testid=charges-table]")).ToBeVisibleAsync(Slow);
        await Page.GetByLabel("Description", new PageGetByLabelOptions { Exact = true }).FillAsync("Retouche de la rayure");
        await Page.GetByLabel("Montant (TTC)", new PageGetByLabelOptions { Exact = true }).FillAsync("120");
        await Page.GetByLabel("Motif", new PageGetByLabelOptions { Exact = true }).FillAsync("Rayure constatée au retour");
        await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Ajouter ce frais" }).ClickAsync();
        await Expect(Page.Locator("tr[data-charge=Other]")).ToBeVisibleAsync();
        await AssertAccessibleAsync();
        await Page.Locator("[data-testid=return-complete]").ClickAsync();

        // Returned: the deposit settles the charge, the rest goes back to the customer, then the file is closed.
        await Expect(Page.Locator("[data-step-panel=payment]")).ToBeVisibleAsync(Slow);
        (await HandoverE2EData.StatusAsync(Host, file.BookingId)).ShouldBe(BookingStatus.Returned);
        await Page.Locator("[data-testid=deposit-retain]").ClickAsync();
        await Expect(Page.Locator("[data-testid=deposit-decision]")).ToHaveCountAsync(0, new LocatorAssertionsToHaveCountOptions { Timeout = 30_000 });
        await Expect(Step("payment")).ToHaveClassAsync(new Regex("handover-step--done"));
        await AssertAccessibleAsync();
        await NextAsync("close");

        await Page.Locator("[data-testid=return-close]").ClickAsync();
        await Expect(Page.Locator("[data-testid=return-closed]")).ToBeVisibleAsync(Slow);
        (await HandoverE2EData.StatusAsync(Host, file.BookingId)).ShouldBe(BookingStatus.Closed);
        await AssertAccessibleAsync();
    }

    private ILocator Step(string key) => Page.Locator($"nav.handover-steps button[data-step={key}]");

    /// <summary>Keeps the errors of the browser console (a circuit error, a failed module) for the failure messages.</summary>
    private void WatchConsole() =>
        Page.Console += (_, message) =>
        {
            if (message.Type is "error" or "warning")
            {
                lock (_console)
                {
                    _console.Add(message.Text);
                }
            }
        };

    private async Task NextAsync(string step)
    {
        await HandoverAccessibility.CloseToastsAsync(Page);
        await Page.Locator("[data-testid=step-next]").ClickAsync();
        await Expect(Page.Locator($"[data-step-panel={step}]")).ToBeVisibleAsync(Slow);
        await Expect(Page.Locator(".handover-heading")).ToBeFocusedAsync();
    }

    /// <summary>Draws a signature with the mouse on the pad of <paramref name="role"/> and records it.</summary>
    private async Task SignAsync(string role)
    {
        // A toast over the pad would take the pointer: closed first; the pad is centred, away from the fixed app bar.
        await HandoverAccessibility.CloseToastsAsync(Page);
        var canvas = Page.Locator($"[data-testid=signature-canvas-{role}]");
        await canvas.EvaluateAsync("canvas => canvas.scrollIntoView({ block: 'center' })");
        await canvas.HoverAsync(new LocatorHoverOptions { Position = new Position { X = 24, Y = 60 } });
        await Page.Mouse.DownAsync();
        var box = (await canvas.BoundingBoxAsync()).ShouldNotBeNull();
        await Page.Mouse.MoveAsync(box.X + (box.Width / 3), box.Y + 24, new MouseMoveOptions { Steps = 8 });
        await Page.Mouse.MoveAsync(box.X + (box.Width * 2 / 3), box.Y + box.Height - 24, new MouseMoveOptions { Steps = 8 });
        await Page.Mouse.UpAsync();
        var section = Page.Locator($"[data-testid=signature-{role}]");
        var save = section.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Valider la signature" });
        try
        {
            await Expect(save).ToBeEnabledAsync();
        }
        catch (PlaywrightException)
        {
            // Why the pad did not answer: whether the drawing happened, and the browser console (e.g. a circuit error).
            var drawn = await canvas.EvaluateAsync<bool>("c => c.getContext('2d').getImageData(0, 0, c.width, c.height).data.some((value, index) => index % 4 === 3 && value !== 0)");
            throw new InvalidOperationException($"The {role} signature was not taken (drawn: {drawn}; console: {string.Join(" | ", _console)}).");
        }

        await save.ClickAsync();
        await Expect(Page.Locator($"[data-testid=signature-{role}][data-signed=true]")).ToBeVisibleAsync(Slow);
    }

    private async Task AssertAccessibleAsync() => await HandoverAccessibility.AssertAsync(Page);
}

/// <summary>A preparer on a phone (390 px): the inspection steps only, the counter steps are the agent's.</summary>
public sealed class HandoverPreparerTests(E2EHost host) : BrowserTest(host)
{
    [Fact]
    public async Task A_preparer_sees_the_inspection_steps_only_on_a_phone()
    {
        var file = await HandoverE2EData.ConfirmedAsync(Host);
        var email = await BackOfficeSession.NewStaffAsync(Host, Roles.Preparer);
        await BackOfficeSession.SignInAsync(Page, email, "/admin/handovers");
        await Expect(Page.Locator("h1[data-ready=true]")).ToHaveTextAsync("Départs et retours", new LocatorAssertionsToHaveTextOptions { Timeout = 30_000 });
        await HandoverAccessibility.AssertAsync(Page);

        await Page.GotoAsync($"/admin/handovers/{file.BookingId}/departure");
        await Expect(Page.Locator("h1[data-ready=true]")).ToHaveTextAsync($"Départ — {file.Number}", new LocatorAssertionsToHaveTextOptions { Timeout = 30_000 });
        var steps = Page.Locator("nav.handover-steps button[data-step]");
        await Expect(steps).ToHaveCountAsync(4);
        (await steps.EvaluateAllAsync<string[]>("buttons => buttons.map(b => b.dataset.step)")).ShouldBe(["vehicle", "photos", "readings", "damages"]);
        await Expect(Page.Locator("[data-step-panel=vehicle]")).ToBeVisibleAsync();
        await Expect(Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Changer de véhicule" })).ToHaveCountAsync(0);
        await HandoverAccessibility.AssertAsync(Page);

        await Page.Locator("[data-testid=vehicle-confirm]").ClickAsync();
        await Expect(Page.Locator("[data-step-panel=photos]")).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30_000 });
        await Page.Locator("[data-testid=photo-input-Front]").SetInputFilesAsync(new FilePayload { Name = "front.png", MimeType = "image/png", Buffer = HandoverE2EData.Png(1200, 900, 5) });
        await Expect(Page.Locator("div[data-slot=Front][data-has-photo=true]")).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30_000 });
        (await Page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= window.innerWidth")).ShouldBeTrue("no horizontal scroll at 390 px");
        await HandoverAccessibility.AssertAsync(Page);
    }
}

/// <summary>The axe check of the hand-over screens (critical and serious violations), toasts closed first.</summary>
internal static class HandoverAccessibility
{
    public static async Task AssertAsync(IPage page)
    {
        // Toasts fade in and out (transient blended colours): closed first, the page content is what is checked.
        await CloseToastsAsync(page);
        var result = await page.RunAxe();
        var blocking = result.Violations
            .Where(v => v.Impact is "critical" or "serious")
            .Select(v => $"{v.Id} ({v.Impact}): {v.Help} [{string.Join(", ", v.Nodes.Select(n => $"{n.Target} {(n.Html.Length > 300 ? n.Html[..300] : n.Html)}"))}]")
            .ToList();
        blocking.ShouldBeEmpty();
    }

    public static async Task CloseToastsAsync(IPage page)
    {
        foreach (var close in await page.Locator(".mud-snackbar button").AllAsync())
        {
            try
            {
                await close.ClickAsync(new LocatorClickOptions { Timeout = 2_000 });
            }
            catch (TimeoutException)
            {
                // Gone by itself meanwhile.
            }
            catch (PlaywrightException)
            {
                // Gone by itself meanwhile.
            }
        }

        await Expect(page.Locator(".mud-snackbar")).ToHaveCountAsync(0, new LocatorAssertionsToHaveCountOptions { Timeout = 10_000 });
    }
}
