using System.Globalization;
using System.Text.RegularExpressions;
using CarRental.Application.Identity.Contracts;
using CarRental.Domain.Bookings;
using CarRental.Domain.Bookings.Handover;
using CarRental.Domain.Common;
using CarRental.E2E.Tests.Bookings.Handover;
using CarRental.E2E.Tests.Support;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace CarRental.E2E.Tests.Scenarios;

/// <summary>
/// The high-season day in the browser (docs/scenarios/journee-haute-saison.md), compact but complete: a customer books
/// on a phone (390 px) through the public funnel up to the simulated bank; the agent, on a 10-inch tablet (1280 × 800),
/// validates the request, sells the last car of the category at the counter while a second tablet is refused, sees both
/// files on the planning once the proposals are applied, hands a car over on the departure wizard, takes a late car back
/// with its late day priced by the engine, and finds the day on the dashboard and the hand-overs screen. No serious or
/// critical accessibility violation on any screen of the way.
/// </summary>
/// <remarks>
/// The public site and the back office serve the demo agency, shared by the browser tests: the scenario sells its own
/// dates (a month ahead, in a category no other test books) and its own customers. The late car is the rental of an
/// agency of its own: the demo agency keeps the single late return that other tests count. A whole day writes much
/// in a few seconds (bookings, photos, payments, a deposit, the audit rows of all of it) and reads the shared screens
/// of the day: it runs alone, once the other browser tests are done (<see cref="HighSeasonDayE2ECollection"/>).
/// </remarks>
[Collection(HighSeasonDayE2ECollection.Name)]
public sealed class HighSeasonDayE2ETests(E2EHost host) : BrowserTest(host)
{
    private const float Patience = 30_000;

    private static readonly LocatorAssertionsToBeVisibleOptions Slow = new() { Timeout = Patience };
    private static readonly LocatorAssertionsToHaveTextOptions SlowText = new() { Timeout = Patience };
    private static readonly LocatorAssertionsToContainTextOptions SlowContains = new() { Timeout = Patience };
    private static readonly LocatorAssertionsToHaveClassOptions SlowClass = new() { Timeout = Patience };
    private static readonly LocatorAssertionsToHaveCountOptions SlowCount = new() { Timeout = Patience };
    private static readonly LocatorAssertionsToBeFocusedOptions SlowFocus = new() { Timeout = Patience };
    private static readonly LocatorAssertionsToBeEnabledOptions SlowEnabled = new() { Timeout = Patience };

    private ILocator Heading => Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Level = 1 });

    [Fact]
    public async Task A_day_at_the_agency_from_a_booking_on_a_phone_to_a_late_return_on_the_tablet()
    {
        await HighSeasonDayE2EData.WaitForAWholeDayAsync();
        var day = TunisTime.Today(TimeProvider.System).AddDays(HighSeasonDayE2EData.DaysAhead);
        var demo = await HighSeasonDayE2EData.DemoAsync(Host);
        await HighSeasonDayE2EData.SellAllButTwoAsync(Host, demo, day);
        var departure = await HighSeasonDayE2EData.WalkInOfTodayAsync(Host, demo);
        var late = await HighSeasonDayE2EData.LateRentalAsync(Host);

        // The customer, on a phone: the funnel of the public site up to the page of the bank.
        var request = await BookOnAPhoneAsync(day);

        // The agent, on a tablet: the request queue, the counter, the planning, the hand-overs, the dashboard.
        await Page.SetViewportSizeAsync(1280, 800);
        var agent = await BackOfficeSession.NewStaffAsync(Host, Roles.Agent);
        await BackOfficeSession.SignInAsync(Page, agent, "/admin/requests");
        await ValidateAsync(request);
        var counter = await SellTheLastCarAtTheCounterAsync(day);
        await ThePlanningShowsBothFilesAsync(day, request, counter);
        await HandOverAsync(departure);
        await TakeTheLateCarBackAsync(late);
        await TheDashboardTellsTheDayAsync(departure, late);
    }

    /// <summary>Results, options, drivers, payment step, the page of the simulated bank, the confirmation: 390 px, no horizontal scroll.</summary>
    private async Task<string> BookOnAPhoneAsync(DateOnly day)
    {
        var suffix = HighSeasonDayE2EData.Unique();
        await Page.GotoAsync($"/fr/reserver?lieu=TUN&depart={HighSeasonDayE2EData.Input(day)}&retour={HighSeasonDayE2EData.Input(day.AddDays(3))}");
        var offer = Page.Locator($"li.offer:has(.offer__code:text-is('{HighSeasonDayE2EData.Category}'))");
        await Expect(offer).ToBeVisibleAsync(Slow);
        await AccessibleOnAPhoneAsync();
        await offer.Locator("form.offer__choose button").ClickAsync();

        await Expect(Page).ToHaveURLAsync(new Regex("/fr/reserver/[A-Za-z0-9_-]{43}/options$"));
        await AccessibleOnAPhoneAsync();
        await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Continuer vers les conducteurs" }).ClickAsync();

        await Expect(Page).ToHaveURLAsync(new Regex("/conducteurs$"));
        await Page.Locator("#main-first-name").FillAsync("Amel");
        await Page.Locator("#main-last-name").FillAsync($"Saison{suffix}");
        await Page.Locator("#main-nationality").SelectOptionAsync("FR");
        await Page.Locator("#main-birth-date").FillAsync("1984-03-14");
        await Page.Locator("#main-phone").FillAsync($"+336{Random.Shared.Next(10_000_000, 99_999_999)}");
        await Page.Locator("#main-email").FillAsync($"saison.{suffix.ToLowerInvariant()}@example.test");
        await Page.Locator("#main-license-number").FillAsync($"L{suffix}");
        await Page.Locator("#main-license-country").SelectOptionAsync("FR");
        await Page.Locator("#main-license-issued-on").FillAsync("2004-06-01");
        await Page.Locator("#main-identity-number").FillAsync($"P{suffix}");
        await Page.Locator("#main-identity-expires-on").FillAsync("2035-01-31");
        await Page.Locator("#consent-documents").CheckAsync();
        await AccessibleOnAPhoneAsync();
        await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Continuer vers le paiement" }).ClickAsync();

        await Expect(Page).ToHaveURLAsync(new Regex("/paiement$"));
        await AccessibleOnAPhoneAsync();
        await Page.GetByLabel("J'ai lu et j'accepte les conditions générales de location").CheckAsync();
        await Page.Locator("[data-testid='funnel-submit']").ClickAsync();

        // The simulated bank (a development page, not part of the site): the pre-authorization is paid.
        await Expect(Page).ToHaveURLAsync(new Regex("/dev/clictopay/pay/"));
        await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Payer" }).ClickAsync();

        await Expect(Page).ToHaveURLAsync(new Regex("/fr/reserver/confirmation/LOC-[0-9]{4}-[0-9]{6}$"));
        await Expect(Heading).ToHaveTextAsync("Demande reçue");
        await Expect(Page.Locator("[data-testid='guarantee-state']")).ToHaveAttributeAsync("data-state", "Held");
        await AccessibleOnAPhoneAsync();
        return Regex.Match(Page.Url, "LOC-[0-9]{4}-[0-9]{6}$").Value;
    }

    /// <summary>The request queue: the checks of the request, then the validation, which captures the guarantee.</summary>
    private async Task ValidateAsync(string request)
    {
        await Expect(Heading).ToHaveTextAsync("Demandes à valider");
        var item = Page.Locator($"li[data-request='{request}']");
        await Expect(item).ToBeVisibleAsync(Slow);
        await item.GetByRole(AriaRole.Button).ClickAsync();
        await Expect(Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = $"Contrôle de la demande {request}" })).ToBeVisibleAsync(Slow);
        await Expect(Page.Locator("li[data-check='Availability'][data-state='Ok']")).ToBeVisibleAsync(Slow);
        await AccessibleAsync();

        await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Valider", Exact = true }).ClickAsync();
        var dialog = Page.GetByRole(AriaRole.Dialog);
        await Expect(dialog).ToBeVisibleAsync(Slow);
        await BackOfficeSession.WaitForOpenDialogAsync(Page);
        await AccessibleAsync();
        await dialog.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Valider", Exact = true }).ClickAsync();

        await Expect(Page.GetByText("Demande validée : réservation confirmée.")).ToBeVisibleAsync(Slow);
        await Expect(item).ToHaveCountAsync(0, SlowCount);
        (await HighSeasonDayE2EData.StatusAsync(Host, request)).ShouldBe(BookingStatus.Confirmed);
    }

    /// <summary>
    /// Two tablets on the quick creation screen show the last car of the category; the first one sells it, the second one
    /// is refused with a clear message and sees the category full. Returns the number of the counter booking.
    /// </summary>
    private async Task<string> SellTheLastCarAtTheCounterAsync(DateOnly day)
    {
        var second = await Context.NewPageAsync();
        await second.SetViewportSizeAsync(1280, 800);
        await PrepareCounterSaleAsync(Page, day);
        await PrepareCounterSaleAsync(second, day);
        await AccessibleAsync();

        await Page.Locator("#booking-save").ClickAsync();
        await Expect(Page).ToHaveURLAsync(new Regex("/admin/bookings/[0-9a-f-]{36}$"), new PageAssertionsToHaveURLOptions { Timeout = Patience });
        await Expect(Heading).ToHaveTextAsync(new Regex("^Dossier LOC-[0-9]{4}-[0-9]{6}$"), SlowText);
        await Expect(Page.Locator("[data-status='Confirmed']").First).ToBeVisibleAsync(Slow);
        var number = (await Heading.TextContentAsync()).ShouldNotBeNull()["Dossier ".Length..];
        await BackOfficeSession.CloseSnackbarsAsync(Page);
        await AccessibleAsync();

        await second.Locator("#booking-save").ClickAsync();
        await Expect(second.GetByRole(AriaRole.Alert).Filter(new LocatorFilterOptions { HasText = "Plus aucun véhicule de cette catégorie n'est disponible pour ces dates." }))
            .ToBeVisibleAsync(Slow);
        var full = second.Locator($"tr[data-category='{HighSeasonDayE2EData.Category}']");
        await Expect(full).ToContainTextAsync("Complet", SlowContains);
        await Expect(full.Locator("input[type=radio]")).ToBeDisabledAsync();
        await second.EvaluateAsync("() => document.activeElement?.blur()");
        await BackOfficeSession.AssertNoSeriousAxeViolationAsync(second);
        await second.CloseAsync();
        return number;
    }

    /// <summary>The counter, the period of the request, the last car of the category chosen, a new customer registered inline.</summary>
    private static async Task PrepareCounterSaleAsync(IPage page, DateOnly day)
    {
        await page.GotoAsync("/admin/bookings/new");
        await BackOfficeSession.WaitUntilInteractiveAsync(page);
        await Expect(page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Level = 1 })).ToHaveTextAsync("Nouvelle réservation");
        await page.Locator(".mud-radio", new PageLocatorOptions { HasText = "Comptoir" }).ClickAsync();
        await page.Locator("#booking-return").FillAsync(HighSeasonDayE2EData.Input(day.AddDays(3)));
        await page.Locator("#booking-pickup").FillAsync(HighSeasonDayE2EData.Input(day));
        var row = page.Locator($"tr[data-category='{HighSeasonDayE2EData.Category}']");
        await Expect(row).ToContainTextAsync("1 restant", SlowContains);
        await row.Locator(".mud-radio").ClickAsync();

        await page.Locator("[data-testid=booking-driver-new]").ClickAsync();
        var lastName = "Comptoir" + HighSeasonDayE2EData.Letters(8);
        await page.Locator("#booking-driver-first-name").FillAsync("Yosra");
        await page.Locator("#booking-driver-last-name").FillAsync(lastName);
        await page.Locator("#booking-driver-phone").FillAsync($"+216 2{Random.Shared.Next(0, 9)} {Random.Shared.Next(100, 999)} {Random.Shared.Next(100, 999)}");
        await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Enregistrer le client" }).ClickAsync();
        await Expect(page.Locator("[data-testid='booking-driver-selected']")).ToHaveTextAsync($"Yosra {lastName}", SlowText);
        await Expect(page.Locator("#booking-save")).ToBeEnabledAsync(SlowEnabled);
        await Expect(page.Locator("#booking-save")).ToHaveTextAsync("Enregistrer la réservation ferme");
        await BackOfficeSession.CloseSnackbarsAsync(page);
    }

    /// <summary>The week of the request on the planning: the proposals of the assigner applied, each file on its own vehicle.</summary>
    private async Task ThePlanningShowsBothFilesAsync(DateOnly day, string request, string counter)
    {
        await Page.GotoAsync($"/admin/planning?from={day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}");
        await Expect(Heading).ToHaveTextAsync("Planning");
        var lane = Page.Locator($"[data-lane='{HighSeasonDayE2EData.Category}']");
        await Expect(lane.Locator($"button[data-booking='{request}']")).ToBeVisibleAsync(Slow);
        await Expect(lane.Locator($"button[data-booking='{counter}']")).ToBeVisibleAsync(Slow);
        await AccessibleAsync();

        // The proposals of the week: the files of the category sold out (all the scenario's) are kept, the other files of the
        // week (demo bookings, shared with other tests) are left as they are. The files of the request and the counter
        // run beyond the week shown: they are planned over their whole period.
        await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Proposer des attributions" }).ClickAsync();
        var dialog = Page.GetByRole(AriaRole.Dialog);
        await Expect(dialog.Locator($"tr[data-proposal='{request}']")).ToBeVisibleAsync(Slow);
        await Expect(dialog.Locator($"tr[data-proposal='{counter}']")).ToBeVisibleAsync(Slow);
        await Expect(dialog.Locator("li", new LocatorLocatorOptions { HasText = request })).ToHaveCountAsync(0);
        await Expect(dialog.Locator("li", new LocatorLocatorOptions { HasText = counter })).ToHaveCountAsync(0);
        await BackOfficeSession.WaitForOpenDialogAsync(Page);
        await AccessibleAsync();
        var ours = dialog.Locator("tr[data-proposal]", new LocatorLocatorOptions { HasText = HighSeasonDayE2EData.Category });
        foreach (var other in await dialog.Locator("tr[data-proposal]", new LocatorLocatorOptions { HasNotText = HighSeasonDayE2EData.Category }).AllAsync())
        {
            await other.GetByRole(AriaRole.Checkbox).UncheckAsync();
        }

        var kept = await ours.CountAsync();
        await dialog.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = $"Appliquer {kept} attribution(s)", Exact = true }).ClickAsync();
        await Expect(ours.Filter(new LocatorFilterOptions { HasText = "Attribuée" })).ToHaveCountAsync(kept, SlowCount);
        await dialog.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Terminé" }).ClickAsync();

        // Both files on vehicles of the category, on two rows, their bars apart.
        var web = Page.Locator($"[data-vehicle] button[data-booking='{request}']");
        var walkIn = Page.Locator($"[data-vehicle] button[data-booking='{counter}']");
        await Expect(web).ToBeVisibleAsync(Slow);
        await Expect(walkIn).ToBeVisibleAsync(Slow);
        await Expect(lane.Locator("button[data-booking]")).ToHaveCountAsync(0, SlowCount);
        var webRow = await web.EvaluateAsync<string>("bar => bar.closest('[data-vehicle]').dataset.vehicle");
        var walkInRow = await walkIn.EvaluateAsync<string>("bar => bar.closest('[data-vehicle]').dataset.vehicle");
        webRow.ShouldNotBe(walkInRow, "the same period cannot sit on one vehicle");
        var a = (await web.BoundingBoxAsync()).ShouldNotBeNull();
        var b = (await walkIn.BoundingBoxAsync()).ShouldNotBeNull();
        (a.Y + a.Height <= b.Y || b.Y + b.Height <= a.Y).ShouldBeTrue("the two bars do not overlap on the grid");
        await BackOfficeSession.CloseSnackbarsAsync(Page);
        await AccessibleAsync();
    }

    /// <summary>The departure on the tablet: originals, vehicle, the eight photos, readings, money (settled), contract signed, keys.</summary>
    private async Task HandOverAsync(HighSeasonDayE2EData.Walkin departure)
    {
        await Page.GotoAsync($"/admin/handovers/{departure.BookingId}/departure");
        await Expect(Page.Locator("h1[data-ready=true]")).ToHaveTextAsync($"Départ — {departure.Number}", SlowText);
        await Expect(Page.Locator("[data-step-panel=originals]")).ToBeVisibleAsync(Slow);
        await AccessibleAsync();
        await Page.GetByText("Pièce d'identité originale vue").ClickAsync();
        await Expect(Page.GetByLabel("Pièce d'identité originale vue")).ToBeCheckedAsync(new LocatorAssertionsToBeCheckedOptions { Timeout = Patience });
        await Page.GetByText("Permis de conduire original vu").ClickAsync();
        await Expect(Step("originals")).ToHaveClassAsync(new Regex("handover-step--done"), SlowClass);
        await NextAsync("vehicle");

        await Page.Locator("[data-testid=vehicle-confirm]").ClickAsync();
        await Expect(Page.Locator("[data-step-panel=photos]")).ToBeVisibleAsync(Slow);
        foreach (var slot in PhotoSlots.Guided)
        {
            await Page.Locator($"[data-testid=photo-input-{slot}]").SetInputFilesAsync(new FilePayload
            {
                Name = $"{slot}.png",
                MimeType = "image/png",
                Buffer = HandoverE2EData.Png(800, 600, (int)slot),
            });
            await Expect(Page.Locator($"div[data-slot={slot}][data-has-photo=true]")).ToBeVisibleAsync(Slow);
        }

        await Expect(Page.Locator("[data-testid=photos-missing]")).ToHaveTextAsync("Toutes les vues guidées sont prises.", SlowText);
        await AccessibleAsync();
        await NextAsync("readings");

        await Page.GetByLabel("Kilométrage", new PageGetByLabelOptions { Exact = true }).FillAsync((departure.Mileage + 15).ToString(CultureInfo.InvariantCulture));
        await Page.Locator("label:has([data-testid=fuel-8])").ClickAsync();
        await Page.Locator("[data-testid=readings-save]").ClickAsync();
        await Expect(Page.Locator("[data-step-panel=damages]")).ToBeVisibleAsync(Slow);
        await AccessibleAsync();

        // The rental was paid at the counter and its deposit taken as a card imprint.
        await NextAsync("payment");
        await Expect(Step("payment")).ToHaveClassAsync(new Regex("handover-step--done"), SlowClass);
        await Expect(Page.Locator("[data-testid=booking-payments][data-ready=true]")).ToBeVisibleAsync(Slow);
        await AccessibleAsync();
        await NextAsync("contract");

        await Expect(Page.Locator("[data-testid=contract-preview]")).ToContainTextAsync(departure.Number, SlowContains);
        await SignAsync("Customer");
        await SignAsync("Agent");
        await Expect(Step("contract")).ToHaveClassAsync(new Regex("handover-step--done"), SlowClass);
        await AccessibleAsync();
        await NextAsync("finish");

        await Expect(Page.Locator("[data-testid=finish-issues]")).ToHaveCountAsync(0, SlowCount);
        await Page.Locator("[data-testid=departure-finish]").ClickAsync();
        await Expect(Page.Locator("[data-testid=departure-done]")).ToBeVisibleAsync(Slow);
        await Expect(Page.Locator("[data-testid=contract-open]")).ToContainTextAsync($"{departure.Number}-v1.pdf", SlowContains);
        await AccessibleAsync();
        (await HighSeasonDayE2EData.StatusAsync(Host, departure.Number)).ShouldBe(BookingStatus.InProgress);
    }

    /// <summary>The return of a car due back yesterday: readings, a photo, the late day priced by the engine, paid, closed.</summary>
    private async Task TakeTheLateCarBackAsync(HighSeasonDayE2EData.Late late)
    {
        await Page.GotoAsync($"/admin/handovers/{late.File.BookingId}/return");
        await Expect(Page.Locator("h1[data-ready=true]")).ToHaveTextAsync($"Retour — {late.File.Number}", SlowText);
        await Expect(Page.Locator("[data-step-panel=readings]")).ToBeVisibleAsync(Slow);
        await AccessibleAsync();
        await Page.GetByLabel("Kilométrage", new PageGetByLabelOptions { Exact = true }).FillAsync("15460");
        await Page.Locator("label:has([data-testid=fuel-8])").ClickAsync();
        await Page.Locator("[data-testid=readings-save]").ClickAsync();
        await Expect(Page.Locator("[data-step-panel=photos]")).ToBeVisibleAsync(Slow);
        await Page.Locator("[data-testid=photo-input-Front]").SetInputFilesAsync(new FilePayload { Name = "front.png", MimeType = "image/png", Buffer = HandoverE2EData.Png(800, 600, 3) });
        await Expect(Page.Locator("div[data-slot=Front][data-has-photo=true]")).ToBeVisibleAsync(Slow);
        await NextAsync("damages");
        await AccessibleAsync();
        await NextAsync("charges");

        // One day late (beyond the tolerance of 59 minutes): the engine prices it at the plan of the agency, 100.000 a day.
        await Expect(Page.Locator("[data-testid=charges-table]")).ToBeVisibleAsync(Slow);
        await Expect(Page.Locator("[data-testid=late-days]")).ToHaveTextAsync("1", SlowText);
        await Expect(Page.Locator("tr[data-charge=LateReturn]")).ToContainTextAsync("100,000", SlowContains);
        await Expect(Page.Locator("tr[data-charge=LateContribution]")).ToContainTextAsync("2,380", SlowContains);
        await AccessibleAsync();
        await Page.Locator("[data-testid=return-complete]").ClickAsync();

        await Expect(Page.Locator("[data-step-panel=payment]")).ToBeVisibleAsync(Slow);
        await Expect(Page.Locator("[data-testid=return-balance]")).ToContainTextAsync("102,380", SlowContains);
        (await HighSeasonDayE2EData.StatusAsync(Host, late.File.Number)).ShouldBe(BookingStatus.Returned);
        await HandoverE2EData.PayAsync(Host, late.File, 102.380m);
        await Page.GotoAsync($"/admin/handovers/{late.File.BookingId}/return?step=payment");
        await Expect(Step("payment")).ToHaveClassAsync(new Regex("handover-step--done"), SlowClass);
        await Expect(Page.Locator("[data-testid=return-balance]")).ToContainTextAsync("(soldé)", SlowContains);
        await AccessibleAsync();
        await NextAsync("close");
        await Page.Locator("[data-testid=return-close]").ClickAsync();
        await Expect(Page.Locator("[data-testid=return-closed]")).ToBeVisibleAsync(Slow);
        await AccessibleAsync();
        (await HighSeasonDayE2EData.StatusAsync(Host, late.File.Number)).ShouldBe(BookingStatus.Closed);
    }

    /// <summary>The dashboard of the day lists the departure done; the hand-overs of the late agency show its return closed.</summary>
    private async Task TheDashboardTellsTheDayAsync(HighSeasonDayE2EData.Walkin departure, HighSeasonDayE2EData.Late late)
    {
        await Page.GotoAsync("/admin");
        await BackOfficeSession.WaitUntilInteractiveAsync(Page);
        var bookings = Page.Locator("section[aria-labelledby='bookings-widget-title']");
        var departed = bookings.Locator($"li[data-agenda='{departure.Number}']");
        await Expect(departed).ToBeVisibleAsync(Slow);
        await Expect(departed).ToContainTextAsync("fait");
        await Expect(Page.GetByTestId("handovers-widget-counts")).ToBeVisibleAsync(Slow);
        await AccessibleAsync();

        await Page.GotoAsync($"/admin/handovers?day={late.PlannedReturnDay.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}");
        await Expect(Page.Locator("h1[data-ready=true]")).ToBeVisibleAsync(Slow);
        await Page.GetByLabel("Agence").ClickAsync();
        await BackOfficeSession.WaitForOpenPopoverAsync(Page);
        await Page.Locator(".mud-popover-open .mud-list-item", new PageLocatorOptions { HasText = late.AgencyName }).ClickAsync();
        var returned = Page.Locator($"[data-testid=handover-returns] li[data-handover='{late.File.Number}']");
        await Expect(returned).ToContainTextAsync("Clôturée", SlowContains);
        await Expect(Page.Locator($"[data-testid=handover-late] li[data-handover='{late.File.Number}']")).ToHaveCountAsync(0);
        await Expect(Page.Locator("[data-testid=handover-late]")).ToContainTextAsync("Aucun retour en retard.");
        await AccessibleAsync();
    }

    private ILocator Step(string key) => Page.Locator($"nav.handover-steps button[data-step={key}]");

    private async Task NextAsync(string step)
    {
        await HandoverAccessibility.CloseToastsAsync(Page);
        await Page.Locator("[data-testid=step-next]").ClickAsync();
        await Expect(Page.Locator($"[data-step-panel={step}]")).ToBeVisibleAsync(Slow);
        await Expect(Page.Locator(".handover-heading")).ToBeFocusedAsync(SlowFocus);
    }

    /// <summary>Draws a signature with the mouse on the pad of <paramref name="role"/> and records it.</summary>
    private async Task SignAsync(string role)
    {
        await HandoverAccessibility.CloseToastsAsync(Page);
        var canvas = Page.Locator($"[data-testid=signature-canvas-{role}]");
        await Expect(Page.Locator($"[data-testid=signature-{role}][data-pad=on]")).ToBeVisibleAsync(Slow);
        await canvas.EvaluateAsync("canvas => canvas.scrollIntoView({ block: 'center' })");
        await canvas.HoverAsync(new LocatorHoverOptions { Position = new Position { X = 24, Y = 60 } });
        await Page.Mouse.DownAsync();
        var box = (await canvas.BoundingBoxAsync()).ShouldNotBeNull();
        await Page.Mouse.MoveAsync(box.X + (box.Width / 3), box.Y + 24, new MouseMoveOptions { Steps = 8 });
        await Page.Mouse.MoveAsync(box.X + (box.Width * 2 / 3), box.Y + box.Height - 24, new MouseMoveOptions { Steps = 8 });
        await Page.Mouse.UpAsync();
        var save = Page.Locator($"[data-testid=signature-{role}]").GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Valider la signature" });
        await Expect(save).ToBeEnabledAsync(SlowEnabled);
        await save.ClickAsync();
        await Expect(Page.Locator($"[data-testid=signature-{role}][data-signed=true]")).ToBeVisibleAsync(Slow);
    }

    private async Task AccessibleAsync() => await HandoverAccessibility.AssertAsync(Page);

    private async Task AccessibleOnAPhoneAsync()
    {
        (await Page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth > window.innerWidth")).ShouldBeFalse($"{Page.Url} scrolls horizontally at 390 px.");
        await BackOfficeSession.AssertNoSeriousAxeViolationAsync(Page);
    }
}

/// <summary>
/// The browser scenario of the day runs without parallelism, after the parallel collections: its burst of writes would
/// push other tests' rows off the first page of the shared screens (audit log, deposits of the day), and its own
/// checks read the dashboard of the shared agency.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class HighSeasonDayE2ECollection
{
    public const string Name = "High-season day (browser)";
}
