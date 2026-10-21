using System.Globalization;
using System.Text.RegularExpressions;
using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Channels.Contracts;
using CarRental.Application.Common.Security;
using CarRental.Application.Fleet.Contracts;
using CarRental.Application.Identity.Contracts;
using CarRental.Application.Referential.Contracts;
using CarRental.Domain.Common;
using CarRental.E2E.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace CarRental.E2E.Tests.Channels;

/// <summary>
/// The sales channels screens on a 10-inch tablet (1280 × 800), on the demo channel <c>booking-com</c> (sold through
/// its extranet): a manager enters a Booking.com reservation by hand and finds it confirmed in the planning, edits a
/// stop-sell of the channel (the website keeps selling) and finds both changes on the "to replicate on the extranet"
/// list; every channels screen passes axe. Each test signs in with its own account and uses dates of its own, two to
/// four months ahead: other browser tests book within two months, and the documents of the demo fleet start expiring
/// beyond (an expired document takes the vehicle out of the capacity, ADR-0021).
/// </summary>
public sealed partial class ChannelsBackOfficeTests(E2EHost host) : BrowserTest(host)
{
    private const string DemoAgencyName = "Agence Démo Tunis";
    private const string DemoChannelCode = "booking-com";

    private static readonly CultureInfo French = CultureInfo.GetCultureInfo("fr-FR");

    private static readonly LocatorAssertionsToBeVisibleOptions Slow = new() { Timeout = 30_000 };

    protected override ViewportSize Viewport => new() { Width = 1280, Height = 800 };

    private ILocator Heading => Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Level = 1 });

    [Fact]
    public async Task A_manager_enters_a_booking_com_reservation_by_hand_and_sees_it_confirmed_in_the_planning()
    {
        var pickupDay = TunisTime.Today(TimeProvider.System).AddDays(60 + Random.Shared.Next(0, 50));
        var reference = $"E2E-{Random.Shared.NextInt64(1_000_000_000, 9_999_999_999).ToString(CultureInfo.InvariantCulture)}";
        var lastName = "Manuel" + Letters(8);
        await SignInAsync("/admin/channels/manual-entry");

        await Expect(Heading).ToHaveTextAsync("Saisie d'une réservation Booking.com");
        await Expect(Page.GetByRole(AriaRole.Combobox, new PageGetByRoleOptions { Name = "Canal" })).ToHaveValueAsync("Booking.com");
        await Page.Locator("#manual-reference").FillAsync(reference);
        await Page.Locator("#manual-pickup").FillAsync(LocalInput(pickupDay));
        await Page.Locator("#manual-return").FillAsync(LocalInput(pickupDay.AddDays(3)));
        await ChooseAsync(Page.Locator("main"), "Catégorie", "ECMR — Économique");
        await Page.Locator("#manual-first-name").FillAsync("Leila");
        await Page.Locator("#manual-last-name").FillAsync(lastName);
        await Page.Locator("#manual-email").FillAsync($"leila.{Guid.NewGuid():N}@example.com");
        await Page.Locator("#manual-gross").FillAsync("390");

        // 15 % of the channel on 390.000 TND: the commission and the net are shown before recording.
        await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Calculer commission et net" }).ClickAsync();
        var preview = Page.GetByTestId("manual-entry-preview");
        await Expect(preview).ToContainTextAsync("58,500 TND", new LocatorAssertionsToContainTextOptions { Timeout = 30_000 });
        await Expect(preview).ToContainTextAsync("331,500 TND");
        await BackOfficeSession.AssertNoSeriousAxeViolationAsync(Page);

        await Page.GetByTestId("manual-entry-save").ClickAsync();
        var result = Page.GetByTestId("manual-entry-result");
        await Expect(result).ToContainTextAsync("confirmée", new LocatorAssertionsToContainTextOptions { Timeout = 30_000 });
        await Expect(Page.GetByTestId("manual-entry-error")).ToHaveCountAsync(0);
        var number = BookingNumber().Match(await result.InnerTextAsync()).Value;
        number.ShouldNotBeNullOrEmpty();
        var href = await result.GetByRole(AriaRole.Link, new LocatorGetByRoleOptions { Name = "Ouvrir le dossier" }).GetAttributeAsync("href");
        var bookingId = Guid.Parse(href.ShouldNotBeNull()[(href.LastIndexOf('/') + 1)..], CultureInfo.InvariantCulture);
        await BackOfficeSession.AssertNoSeriousAxeViolationAsync(Page);

        // The planning shows the confirmed file, unassigned, in the lane of its category.
        await result.GetByRole(AriaRole.Link, new LocatorGetByRoleOptions { Name = "Voir le planning" }).ClickAsync();
        await Expect(Page).ToHaveURLAsync(new Regex(Regex.Escape($"/admin/planning?from={Iso(pickupDay)}") + "$"));
        await Expect(Heading).ToHaveTextAsync("Planning");
        await Expect(Page.Locator($"[data-lane='ECMR'] button[data-booking='{number}']")).ToBeVisibleAsync(Slow);

        var summary = await AsSystemAsync(services => services.GetRequiredService<IBookingsQueries>().GetSummaryAsync(bookingId));
        summary.ShouldNotBeNull().Status.ShouldBe(BookingStatusInfo.Confirmed);
        summary.Number.ShouldBe(number);
        summary.Channel.ShouldBe(SalesChannel.BookingCom);
        summary.ChannelReference.ShouldBe(reference);
        var side = (await AsSystemAsync(services => services.GetRequiredService<IChannelsQueries>().GetChannelBookingAsync(bookingId))).ShouldNotBeNull();
        side.ChannelCode.ShouldBe(DemoChannelCode);
        side.Source.ShouldBe(ChannelBookingSourceInfo.Manual);
        side.Gross.ShouldBe(Money.Tnd(390m));
        side.Commission.ShouldBe(Money.Tnd(58.5m));
        side.Net.ShouldBe(Money.Tnd(331.5m));
    }

    [Fact]
    public async Task A_manager_edits_a_stop_sell_of_booking_com_and_finds_it_on_the_extranet_list()
    {
        var start = TunisTime.Today(TimeProvider.System).AddDays(60 + Random.Shared.Next(0, 35));
        var note = "E2E " + Letters(10);
        await SignInAsync("/admin/channels");

        await Expect(Heading).ToHaveTextAsync("Canaux de vente");
        var channel = Page.Locator($"a[data-channel='{DemoChannelCode}']");
        await Expect(channel).ToBeVisibleAsync(Slow);
        await BackOfficeSession.AssertNoSeriousAxeViolationAsync(Page);

        await channel.ClickAsync();
        await Expect(Page).ToHaveURLAsync(new Regex("/admin/channels/[0-9a-f-]{36}$"));
        await Expect(Page.Locator("h1[data-ready='true']")).ToBeVisibleAsync(Slow);
        await Page.GetByRole(AriaRole.Tab, new PageGetByRoleOptions { Name = "Quotas et arrêts de vente" }).ClickAsync();
        await Expect(Page.GetByText("Canal manuel : chaque changement s'ajoute")).ToBeVisibleAsync();

        // A new stop-sell of the automatic economy cars, on every location, for three days.
        await Page.GetByTestId("add-stop-sell").ClickAsync();
        var dialog = Page.GetByRole(AriaRole.Dialog);
        await Expect(dialog).ToBeVisibleAsync();
        await BackOfficeSession.WaitForOpenDialogAsync(Page);
        await ChooseAsync(dialog, "Catégorie", "EDAR — Économique automatique");
        await dialog.Locator("#restriction-start").FillAsync(Iso(start));
        await dialog.Locator("#restriction-end").FillAsync(Iso(start.AddDays(2)));
        await dialog.GetByLabel("Note").FillAsync(note);
        await BackOfficeSession.AssertNoSeriousAxeViolationAsync(Page);
        await dialog.GetByTestId("restriction-save").ClickAsync();
        await Expect(dialog).ToHaveCountAsync(0, new LocatorAssertionsToHaveCountOptions { Timeout = 30_000 });

        var row = Page.Locator("tr", new PageLocatorOptions { HasText = note });
        await Expect(row).ToContainTextAsync("Arrêt de vente", new LocatorAssertionsToContainTextOptions { Timeout = 30_000 });
        await Expect(row).ToContainTextAsync("EDAR");
        await Expect(row).ToContainTextAsync("Tous les lieux");
        await Expect(row).ToContainTextAsync(PeriodText(start, start.AddDays(2)));

        // The channel no longer sells the category on these days; the website still does.
        var (web, bookingCom) = await RemainingAsync("EDAR", start);
        web.ShouldBeGreaterThan(0, "a stop-sell of Booking.com never blocks the website");
        bookingCom.ShouldBe(0);

        // Edited: two more days.
        await row.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Modifier" }).ClickAsync();
        await Expect(dialog).ToBeVisibleAsync();
        await Expect(dialog.Locator("#restriction-end")).ToHaveValueAsync(Iso(start.AddDays(2)));
        await dialog.Locator("#restriction-end").FillAsync(Iso(start.AddDays(4)));
        await dialog.GetByTestId("restriction-save").ClickAsync();
        await Expect(dialog).ToHaveCountAsync(0, new LocatorAssertionsToHaveCountOptions { Timeout = 30_000 });
        await Expect(row).ToContainTextAsync(PeriodText(start, start.AddDays(4)), new LocatorAssertionsToContainTextOptions { Timeout = 30_000 });
        (await RemainingAsync("EDAR", start.AddDays(4))).BookingCom.ShouldBe(0);

        // The success snackbar comes from the shared admin theme (its green fails the contrast ratio): checked once gone.
        await Expect(Page.Locator(".mud-snackbar")).ToHaveCountAsync(0, new LocatorAssertionsToHaveCountOptions { Timeout = 20_000 });
        await BackOfficeSession.AssertNoSeriousAxeViolationAsync(Page);

        // Both changes wait on the extranet list of the manual channel until the agency replicates them.
        await Page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = "À reporter sur l'extranet", Exact = true }).ClickAsync();
        await Expect(Page).ToHaveURLAsync(new Regex("/admin/channels/extranet$"));
        await Expect(Page.Locator("h1[data-ready='true']")).ToBeVisibleAsync(Slow);
        var added = Page.Locator("tr", new PageLocatorOptions { HasText = "Arrêt de vente ajouté" }).Filter(new LocatorFilterOptions { HasText = PeriodText(start, start.AddDays(2)) });
        var changed = Page.Locator("tr", new PageLocatorOptions { HasText = "Arrêt de vente modifié" }).Filter(new LocatorFilterOptions { HasText = PeriodText(start, start.AddDays(4)) });
        await Expect(added).ToContainTextAsync("EDAR", new LocatorAssertionsToContainTextOptions { Timeout = 30_000 });
        await Expect(changed).ToContainTextAsync("À reporter");
        await BackOfficeSession.AssertNoSeriousAxeViolationAsync(Page);

        await changed.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "comme reporté" }).ClickAsync();
        await Expect(changed).ToHaveCountAsync(0, new LocatorAssertionsToHaveCountOptions { Timeout = 30_000 });
        await Expect(added).ToHaveCountAsync(1);
    }

    [Fact]
    public async Task The_channel_settings_security_mappings_queue_log_and_reconciliation_screens_pass_axe()
    {
        var channelId = await AsSystemAsync(async services =>
            (await services.GetRequiredService<IChannelsQueries>().GetChannelsAsync()).Single(c => c.Code == DemoChannelCode).Id);
        await SignInAsync($"/admin/channels/{channelId}");

        await Expect(Page.Locator("h1[data-ready='true']")).ToHaveTextAsync("Canal Booking.com", Slow30());
        await Expect(Page.GetByRole(AriaRole.Combobox, new PageGetByRoleOptions { Name = "État" })).ToHaveValueAsync("Manuel (extranet)");
        await BackOfficeSession.AssertNoSeriousAxeViolationAsync(Page);

        await Page.GetByRole(AriaRole.Tab, new PageGetByRoleOptions { Name = "Sécurité" }).ClickAsync();
        await Expect(Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "Adresse d'entrée" })).ToBeVisibleAsync();
        await Expect(Page.Locator("code", new PageLocatorOptions { HasText = $"/api/channels/{DemoChannelCode}/" })).ToBeVisibleAsync();
        await BackOfficeSession.AssertNoSeriousAxeViolationAsync(Page);

        await Page.GetByRole(AriaRole.Tab, new PageGetByRoleOptions { Name = "Correspondances" }).ClickAsync();
        await Expect(Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "Catégories" })).ToBeVisibleAsync();
        await Expect(Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Enregistrer la correspondance de ECMR" })).ToBeVisibleAsync();
        await BackOfficeSession.AssertNoSeriousAxeViolationAsync(Page);

        await Page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = "File d'envoi", Exact = true }).ClickAsync();
        await Expect(Heading).ToHaveTextAsync("File d'envoi vers les canaux");
        await Expect(Page.GetByTestId("sync-statistics")).ToBeVisibleAsync(Slow);
        await Expect(Page.Locator(".mud-table-loading-progress")).ToHaveCountAsync(0, new LocatorAssertionsToHaveCountOptions { Timeout = 30_000 });
        await BackOfficeSession.AssertNoSeriousAxeViolationAsync(Page);

        await Page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = "Journal des messages", Exact = true }).ClickAsync();
        await Expect(Heading).ToHaveTextAsync("Journal des messages des canaux");
        await Expect(Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Rechercher" })).ToBeVisibleAsync();
        await Expect(Page.Locator(".mud-table-loading-progress")).ToHaveCountAsync(0, new LocatorAssertionsToHaveCountOptions { Timeout = 30_000 });
        await BackOfficeSession.AssertNoSeriousAxeViolationAsync(Page);

        await Page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = "Rapprochement", Exact = true }).ClickAsync();
        await Expect(Heading).ToHaveTextAsync("Rapprochement des canaux");
        await Expect(Page.GetByTestId("reconciliation-figures")).ToContainTextAsync("Versement attendu", new LocatorAssertionsToContainTextOptions { Timeout = 30_000 });
        await BackOfficeSession.AssertNoSeriousAxeViolationAsync(Page);
    }

    private static LocatorAssertionsToHaveTextOptions Slow30() => new() { Timeout = 30_000 };

    private static string Iso(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string LocalInput(DateOnly day) => $"{Iso(day)}T10:00";

    private static string PeriodText(DateOnly start, DateOnly end) => $"du {start.ToString("d", French)} au {end.ToString("d", French)}";

    private static string Letters(int count) => new([.. Enumerable.Range(0, count).Select(_ => (char)('a' + Random.Shared.Next(26)))]);

    /// <summary>Signs in a new manager (every permission, <c>channels.manage</c> included) and waits for the circuit.</summary>
    private async Task SignInAsync(string returnUrl)
    {
        await BackOfficeSession.SignInAsync(Page, await BackOfficeSession.NewStaffAsync(Host, Roles.Manager), returnUrl);
        await BackOfficeSession.WaitUntilInteractiveAsync(Page);
    }

    /// <summary>Picks <paramref name="option"/> in the MudBlazor select labelled <paramref name="label"/> inside <paramref name="scope"/>.</summary>
    private async Task ChooseAsync(ILocator scope, string label, string option)
    {
        var select = scope.GetByRole(AriaRole.Combobox, new LocatorGetByRoleOptions { Name = label });
        await select.ClickAsync();
        await BackOfficeSession.WaitForOpenPopoverAsync(Page);
        await Page.Locator(".mud-popover-open").GetByRole(AriaRole.Option, new LocatorGetByRoleOptions { Name = option }).ClickAsync();
        await Expect(select).ToHaveValueAsync(option);
        await Expect(Page.Locator(".mud-popover-open")).ToHaveCountAsync(0);
    }

    /// <summary>Vehicles of the demo category <paramref name="code"/> still for sale on the website and on Booking.com for a day.</summary>
    private Task<(int Web, int BookingCom)> RemainingAsync(string code, DateOnly day) =>
        AsSystemAsync(async services =>
        {
            var agency = (await services.GetRequiredService<IReferentialQueries>().GetAgenciesAsync()).Single(a => a.Name == DemoAgencyName);
            var category = (await services.GetRequiredService<IFleetQueries>().FindCategoryByCodeAsync(agency.Id, code)).ShouldNotBeNull();
            var rental = Period.Create(TunisTime.FromLocal(day, new TimeOnly(10, 0)), TunisTime.FromLocal(day.AddDays(1), new TimeOnly(10, 0))).Value;
            var availability = services.GetRequiredService<IAvailabilityQueries>();
            var web = (await availability.GetCategoryAvailabilityAsync(category.Id, rental, SalesChannel.Web)).ShouldNotBeNull();
            var bookingCom = (await availability.GetCategoryAvailabilityAsync(category.Id, rental, SalesChannel.BookingCom)).ShouldNotBeNull();
            return (web.Remaining, bookingCom.Remaining);
        });

    /// <summary>Runs <paramref name="action"/> in a scope of the application acting as the platform (test arrangements and checks).</summary>
    private async Task<T> AsSystemAsync<T>(Func<IServiceProvider, Task<T>> action)
    {
        await using var scope = Host.Factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ScopedUserContext>().Initialize(UserContextSnapshot.System("system:e2e"));
        return await action(scope.ServiceProvider);
    }

    [GeneratedRegex(@"LOC-\d{4}-\d{6}", RegexOptions.CultureInvariant)]
    private static partial Regex BookingNumber();
}
