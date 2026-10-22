using System.Globalization;
using System.Text.RegularExpressions;
using CarRental.Application.Bookings;
using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Common.Security;
using CarRental.Application.Customers.Contracts;
using CarRental.Application.Fleet.Contracts;
using CarRental.Application.Identity.Contracts;
using CarRental.Application.Pricing.Contracts;
using CarRental.Application.Referential.Contracts;
using CarRental.Domain.Common;
using CarRental.E2E.Tests.Support;
using Deque.AxeCore.Playwright;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace CarRental.E2E.Tests.Bookings;

/// <summary>
/// The bookings screens on a 10-inch tablet (1280 × 800), on the demo agency: a phone option taken on the quick creation
/// screen, a web request validated from the request queue, a vehicle assigned from the planning with the keyboard, and
/// no serious or critical accessibility violation on these screens. Each test signs in with its own account.
/// </summary>
public sealed class BookingsBackOfficeTests(E2EHost host) : BrowserTest(host)
{
    private const string DemoAgencyName = "Agence Démo Tunis";

    private static readonly LocatorAssertionsToBeVisibleOptions Slow = new() { Timeout = 30_000 };

    protected override ViewportSize Viewport => new() { Width = 1280, Height = 800 };

    private ILocator Heading => Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Level = 1 });

    [Fact]
    public async Task An_agent_takes_a_phone_option_for_a_new_customer_on_the_quick_creation_screen()
    {
        var pickup = TunisTime.Today(TimeProvider.System).AddDays(44);
        var lastName = "Essai" + new string([.. Enumerable.Range(0, 8).Select(_ => (char)('a' + Random.Shared.Next(26)))]);
        await SignInAsync(Roles.Agent, "/admin/bookings/new");

        await Expect(Heading).ToHaveTextAsync("Nouvelle réservation");
        await Page.Locator("#booking-return").FillAsync(LocalInput(pickup.AddDays(3)));
        await Page.Locator("#booking-pickup").FillAsync(LocalInput(pickup));
        var row = Page.Locator("tr[data-category='ECMR']");
        await Expect(row).ToContainTextAsync("restant", new LocatorAssertionsToContainTextOptions { Timeout = 30_000 });
        await Expect(row.Locator("td").Nth(3)).ToHaveTextAsync("3", new LocatorAssertionsToHaveTextOptions { Timeout = 30_000 });
        await row.Locator(".mud-radio").ClickAsync();
        await ExpectNoSeriousAccessibilityViolationAsync();

        // Keyboard shortcuts: the customer search, then the inline registration of a new customer.
        await Page.Keyboard.PressAsync("Alt+Shift+KeyC");
        await Expect(Page.Locator("#booking-driver")).ToBeFocusedAsync();
        await Page.Keyboard.PressAsync("Alt+Shift+KeyN");
        await Page.Locator("#booking-driver-first-name").FillAsync("Yosra");
        await Page.Locator("#booking-driver-last-name").FillAsync(lastName);
        await Page.Locator("#booking-driver-phone").FillAsync($"+216 2{Random.Shared.Next(0, 9)} {Random.Shared.Next(100, 999)} {Random.Shared.Next(100, 999)}");
        await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Enregistrer le client" }).ClickAsync();
        await Expect(Page.Locator("[data-testid='booking-driver-selected']")).ToHaveTextAsync($"Yosra {lastName}", new LocatorAssertionsToHaveTextOptions { Timeout = 30_000 });
        await Expect(Page.GetByText("Nouveau client créé.")).ToBeVisibleAsync();

        await Expect(Page.Locator("#booking-save")).ToBeEnabledAsync();
        await Page.Locator("#booking-save").ClickAsync();

        await Expect(Page).ToHaveURLAsync(new Regex("/admin/bookings/[0-9a-f-]{36}$"), new PageAssertionsToHaveURLOptions { Timeout = 30_000 });
        await Expect(Heading).ToHaveTextAsync(new Regex("^Dossier LOC-[0-9]{4}-[0-9]{6}$"), new LocatorAssertionsToHaveTextOptions { Timeout = 30_000 });
        await Expect(Page.Locator("[data-status='OptionHeld']").First).ToBeVisibleAsync();
        await Expect(Page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = $"Yosra {lastName}" })).ToBeVisibleAsync();
        await Expect(Page.Locator("[data-event-type='Created']")).ToBeVisibleAsync();

        // The success snackbar comes from the shared admin theme (its green fails the contrast ratio): checked once gone.
        await Expect(Page.Locator(".mud-snackbar")).ToHaveCountAsync(0, new LocatorAssertionsToHaveCountOptions { Timeout = 20_000 });
        await ExpectNoSeriousAccessibilityViolationAsync();
    }

    [Fact]
    public async Task An_agent_validates_a_web_request_from_the_request_queue()
    {
        var request = await SubmitWebRequestAsync(TunisTime.Today(TimeProvider.System).AddDays(50));
        await SignInAsync(Roles.Agent, "/admin/requests");

        await Expect(Heading).ToHaveTextAsync("Demandes à valider");
        var item = Page.Locator($"li[data-request='{request.Number}']");
        await Expect(item).ToBeVisibleAsync(Slow);
        await item.GetByRole(AriaRole.Button).ClickAsync();
        await Expect(Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = $"Contrôle de la demande {request.Number}" })).ToBeVisibleAsync(Slow);
        await Expect(Page.Locator("li[data-check='Availability'][data-state='Ok']")).ToBeVisibleAsync(Slow);
        await Expect(Page.Locator("li[data-check='CustomerCheck'][data-state='Ok']")).ToBeVisibleAsync();
        await ExpectNoSeriousAccessibilityViolationAsync();

        await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Valider", Exact = true }).ClickAsync();
        var dialog = Page.GetByRole(AriaRole.Dialog);
        await Expect(dialog).ToBeVisibleAsync();
        await dialog.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Valider", Exact = true }).ClickAsync();

        // Submitted without a pre-authorization (no payment page): the validation asks for the payment by link (ADR-0051).
        await Expect(Page.GetByText("Demande validée : en attente du paiement par lien.")).ToBeVisibleAsync(Slow);
        await Expect(item).ToHaveCountAsync(0, new LocatorAssertionsToHaveCountOptions { Timeout = 30_000 });
        var summary = await AsSystemAsync(services => services.GetRequiredService<IBookingsQueries>().GetSummaryAsync(request.BookingId));
        summary.ShouldNotBeNull().Status.ShouldBe(BookingStatusInfo.AwaitingPayment);
    }

    [Fact]
    public async Task A_manager_assigns_a_vehicle_from_the_planning_with_the_keyboard()
    {
        var pickupDay = TunisTime.Today(TimeProvider.System).AddDays(22);
        var booking = await CreateConfirmedAsync("IFMR", pickupDay);
        await SignInAsync(Roles.Manager, $"/admin/planning?from={pickupDay.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}");

        await Expect(Heading).ToHaveTextAsync("Planning");
        var unassigned = Page.Locator($"[data-lane='IFMR'] button[data-booking='{booking.Number}']");
        await Expect(unassigned).ToBeVisibleAsync(Slow);
        await ExpectNoSeriousAccessibilityViolationAsync();

        await unassigned.FocusAsync();
        await Page.Keyboard.PressAsync("Enter");
        await Expect(Page.Locator("[data-testid='planning-selected']")).ToHaveTextAsync(booking.Number);
        await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Attribuer un véhicule…" }).ClickAsync();
        var dialog = Page.GetByRole(AriaRole.Dialog);
        await Expect(dialog.Locator("[data-vehicle]").First).ToBeVisibleAsync(Slow);
        await dialog.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Attribuer", Exact = true }).ClickAsync();

        await Expect(Page.Locator($"[data-vehicle] button[data-booking='{booking.Number}']")).ToBeVisibleAsync(Slow);
        await Expect(unassigned).ToHaveCountAsync(0);
        var summary = await AsSystemAsync(services => services.GetRequiredService<IBookingsQueries>().GetSummaryAsync(booking.BookingId));
        summary.ShouldNotBeNull().AssignedVehicleId.ShouldNotBeNull();
    }

    [Fact]
    public async Task A_manager_drags_a_file_onto_a_vehicle_then_back_onto_the_lane_of_its_category()
    {
        var pickupDay = TunisTime.Today(TimeProvider.System).AddDays(52);
        var booking = await CreateConfirmedAsync("CDMR", pickupDay);
        await SignInAsync(Roles.Manager, $"/admin/planning?from={pickupDay.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}");

        var unassigned = Page.Locator($"[data-lane='CDMR'] button[data-booking='{booking.Number}']");
        await Expect(unassigned).ToBeVisibleAsync(Slow);
        var vehicleRow = Page.Locator("[data-category='CDMR'] ~ [data-vehicle]").First;
        var plate = await vehicleRow.GetAttributeAsync("data-vehicle");
        await DragAsync(unassigned, vehicleRow.Locator(".planning-track"));

        var assigned = Page.Locator($"[data-vehicle='{plate}'] button[data-booking='{booking.Number}']");
        await Expect(assigned).ToBeVisibleAsync(Slow);
        var summary = await AsSystemAsync(services => services.GetRequiredService<IBookingsQueries>().GetSummaryAsync(booking.BookingId));
        summary.ShouldNotBeNull().AssignedVehicleId.ShouldNotBeNull();

        await DragAsync(assigned, Page.Locator("[data-lane='CDMR'] .planning-track"));
        await Expect(unassigned).ToBeVisibleAsync(Slow);
        summary = await AsSystemAsync(services => services.GetRequiredService<IBookingsQueries>().GetSummaryAsync(booking.BookingId));
        summary.ShouldNotBeNull().AssignedVehicleId.ShouldBeNull();
    }

    [Fact]
    public async Task The_list_a_file_and_the_dashboard_show_the_demo_bookings_accessibly()
    {
        await SignInAsync(Roles.Manager, "/admin/bookings");

        await Expect(Heading).ToHaveTextAsync("Réservations");
        var firstLink = Page.Locator("table a[href^='/admin/bookings/']").First;
        await Expect(firstLink).ToBeVisibleAsync(Slow);
        await ExpectNoSeriousAccessibilityViolationAsync();

        await Page.GetByLabel("Rechercher").FillAsync("LOC-");
        await Expect(firstLink).ToBeVisibleAsync(Slow);
        await firstLink.ClickAsync();
        await Expect(Page).ToHaveURLAsync(new Regex("/admin/bookings/[0-9a-f-]{36}$"));
        await Expect(Heading).ToHaveTextAsync(new Regex("^Dossier LOC-"), new LocatorAssertionsToHaveTextOptions { Timeout = 30_000 });
        await Expect(Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "Chronologie" })).ToBeVisibleAsync();
        await ExpectNoSeriousAccessibilityViolationAsync();

        await Page.GotoAsync("/admin");
        var widget = Page.Locator("section[aria-labelledby='bookings-widget-title']");
        await Expect(widget.GetByRole(AriaRole.Heading, new LocatorGetByRoleOptions { Name = "Retours en retard (1)" })).ToBeVisibleAsync(Slow);
        await BackOfficeSession.WaitForTransitionsAsync(Page);
        var result = await widget.RunAxe();
        result.Violations.Where(v => v.Impact is "critical" or "serious").Select(v => v.Id).ShouldBeEmpty();
    }

    /// <summary>
    /// Drags <paramref name="bar"/> straight down or up onto <paramref name="track"/>, at the time of the bar, with the mouse.
    /// The week scrolls sideways: bringing the centre of the target into view while the button is pressed (what
    /// <c>DragToAsync</c> does) would scroll the grid under the pointer, and the browser then never starts the drag.
    /// </summary>
    private async Task DragAsync(ILocator bar, ILocator track)
    {
        await bar.EvaluateAsync("bar => bar.scrollIntoView({ block: 'center', inline: 'center' })");
        var from = (await bar.BoundingBoxAsync()).ShouldNotBeNull();
        var to = (await track.BoundingBoxAsync()).ShouldNotBeNull();
        var x = from.X + (from.Width / 2);
        await Page.Mouse.MoveAsync(x, from.Y + (from.Height / 2));
        await Page.Mouse.DownAsync();
        await Page.Mouse.MoveAsync(x, to.Y + (to.Height / 2), new MouseMoveOptions { Steps = 10 });
        await Page.Mouse.UpAsync();
    }

    private static string LocalInput(DateOnly day) => $"{day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}T10:00";

    private async Task<BookingRequestResult> SubmitWebRequestAsync(DateOnly pickupDay)
    {
        var demo = await DemoAsync("ECMR", "sami.trabelsi@example.com");
        return await AsSystemAsync(async services =>
        {
            var requests = services.GetRequiredService<IBookingRequests>();
            var price = new PriceRequest(
                demo.AgencyId,
                demo.CategoryId,
                demo.OfficeId,
                demo.OfficeId,
                TunisTime.FromLocal(pickupDay, new TimeOnly(10, 0)),
                TunisTime.FromLocal(pickupDay.AddDays(3), new TimeOnly(10, 0)),
                SalesChannel.Web);
            var submitted = await requests.SubmitRequestAsync(new BookingRequestDraft(SalesChannel.Web, Price: price), new BookingRequestParties(demo.PersonId));
            return submitted.Value;
        });
    }

    private async Task<QuickBookingResult> CreateConfirmedAsync(string category, DateOnly pickupDay)
    {
        var demo = await DemoAsync(category, "mohamed.gharbi@example.com");
        return await AsSystemAsync(async services =>
        {
            var created = await services.GetRequiredService<QuickBookingService>().CreateAsync(new QuickBookingRequest(
                demo.AgencyId,
                SalesChannel.Phone,
                demo.CategoryId,
                demo.OfficeId,
                demo.OfficeId,
                TunisTime.FromLocal(pickupDay, new TimeOnly(10, 0)),
                TunisTime.FromLocal(pickupDay.AddDays(1), new TimeOnly(10, 0)),
                demo.PersonId,
                QuickBookingOutcome.Confirmed));
            return created.Value;
        });
    }

    /// <summary>The demo agency, its office, a category by code and a demo person by e-mail.</summary>
    private Task<(Guid AgencyId, Guid OfficeId, Guid CategoryId, Guid PersonId)> DemoAsync(string category, string email) =>
        AsSystemAsync(async services =>
        {
            var referential = services.GetRequiredService<IReferentialQueries>();
            var agency = (await referential.GetAgenciesAsync()).Single(a => a.Name == DemoAgencyName);
            var office = (await referential.GetLocationsAsync(agency.Id)).Single(l => l.Code == "AGC");
            var categoryId = (await services.GetRequiredService<IFleetQueries>().FindCategoryByCodeAsync(agency.Id, category)).ShouldNotBeNull().Id;
            var person = (await services.GetRequiredService<ICustomersQueries>().SearchPersonsAsync(email, 5)).Single(p => p.Email == email);
            return (agency.Id, office.Id, categoryId, person.Id);
        });

    /// <summary>Runs <paramref name="action"/> in a scope of the application acting as the platform (test arrangements).</summary>
    private async Task<T> AsSystemAsync<T>(Func<IServiceProvider, Task<T>> action)
    {
        await using var scope = Host.Factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ScopedUserContext>().Initialize(UserContextSnapshot.System("system:e2e"));
        return await action(scope.ServiceProvider);
    }

    private async Task ExpectNoSeriousAccessibilityViolationAsync()
    {
        await BackOfficeSession.WaitForTransitionsAsync(Page);
        var result = await Page.RunAxe();
        var blocking = result.Violations
            .Where(v => v.Impact is "critical" or "serious")
            .Select(v => $"{v.Id} ({v.Impact}): {v.Help} [{string.Join(", ", v.Nodes.Select(n => $"{n.Target} {n.Html}"))}]")
            .ToList();
        blocking.ShouldBeEmpty();
    }

    /// <summary>Signs in a member of the demo agency with <paramref name="role"/>, an account of the test's own.</summary>
    private async Task SignInAsync(string role, string returnUrl) =>
        await BackOfficeSession.SignInAsync(Page, await BackOfficeSession.NewStaffAsync(Host, role), returnUrl);
}
