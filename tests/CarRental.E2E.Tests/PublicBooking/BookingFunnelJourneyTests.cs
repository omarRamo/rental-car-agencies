using System.Globalization;
using System.Text.RegularExpressions;
using CarRental.Application.Bookings.PublicAccess;
using CarRental.Application.Common.Security;
using CarRental.Application.Fleet.Contracts;
using CarRental.Application.Referential.Contracts;
using CarRental.Domain.Common;
using CarRental.Testing;
using Deque.AxeCore.Playwright;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace CarRental.E2E.Tests.PublicBooking;

/// <summary>
/// The booking funnel and "Ma réservation" on a 390 px phone (ADR-0049 to ADR-0051): the whole French funnel with photos
/// of the documents and the payment page of the simulated bank, the English steps, the access link of the e-mail,
/// keyboard-only use of the payment step, accessibility (axe: nothing serious or critical) and no horizontal scroll.
/// </summary>
public sealed class BookingFunnelJourneyTests(E2EHost host) : BrowserTest(host)
{
    /// <summary>A real 1×1 PNG: the upload module turns it into a JPEG in the browser.</summary>
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");

    private static string LocalDate(int days) =>
        TunisTime.Today(TimeProvider.System).AddDays(days).ToDateTime(new TimeOnly(10, 0)).ToString("yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture);

    private static string Unique() =>
        new([.. Guid.NewGuid().ToString("N")[..8].Select(c => char.IsAsciiDigit(c) ? (char)('G' + (c - '0')) : char.ToUpperInvariant(c))]);

    [Fact]
    public async Task A_visitor_books_in_french_with_photos_and_holds_the_guarantee_on_the_page_of_the_bank()
    {
        var suffix = Unique();
        await Page.GotoAsync($"/fr/reserver?lieu=TUN&depart={LocalDate(60)}&retour={LocalDate(63)}");
        await Page.Locator("li.offer:has(.offer__code:text-is('ECMR')) form.offer__choose button").ClickAsync();

        // Step 1: options.
        await Expect(Page).ToHaveURLAsync(new Regex("/fr/reserver/[A-Za-z0-9_-]{43}/options$"));
        await Expect(Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Level = 1 })).ToHaveTextAsync("Options et prix");
        await ShouldBeAccessibleOnAPhoneAsync();
        var total = await Page.Locator("[data-testid='funnel-total']").TextContentAsync();
        await Page.GetByLabel("Siège enfant").SelectOptionAsync("1");
        await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Mettre à jour le prix" }).ClickAsync();
        await Expect(Page.Locator("[data-testid='funnel-total']")).Not.ToHaveTextAsync(total!);
        await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Continuer vers les conducteurs" }).ClickAsync();

        // Step 2: drivers, photos sent by the upload module.
        await Expect(Page).ToHaveURLAsync(new Regex("/conducteurs$"));
        await ShouldBeAccessibleOnAPhoneAsync();
        await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Continuer vers le paiement" }).ClickAsync();
        await Expect(Page.Locator("[data-testid='drivers-errors']")).ToBeFocusedAsync();
        await ShouldBeAccessibleOnAPhoneAsync();

        await FillDriverAsync(suffix, $"e2e.{suffix.ToLowerInvariant()}@example.test");
        await Page.Locator("#photo-main-license-front").SetInputFilesAsync(new FilePayload { Name = "permis.png", MimeType = "image/png", Buffer = Png });
        await Expect(Page.Locator("#photo-main-license-front-status")).ToHaveTextAsync("Photo reçue.");
        await Expect(Page.Locator("[data-upload-slot='main.license.front'] img[data-upload-preview]")).ToBeVisibleAsync();
        await Page.Locator("#photo-main-identity-front").SetInputFilesAsync(new FilePayload { Name = "passeport.png", MimeType = "image/png", Buffer = Png });
        await Expect(Page.Locator("#photo-main-identity-front-status")).ToHaveTextAsync("Photo reçue.");
        await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Continuer vers le paiement" }).ClickAsync();

        // Step 3: terms, anti-robot check (offline in tests), then the payment page of the simulated bank.
        await Expect(Page).ToHaveURLAsync(new Regex("/paiement$"));
        await Expect(Page.GetByText($"Conducteur principal : Amel E2E{suffix}")).ToBeVisibleAsync();
        await ShouldBeAccessibleOnAPhoneAsync();
        await Page.GetByLabel("J'ai lu et j'accepte les conditions générales de location").CheckAsync();
        await Page.Locator("[data-testid='funnel-submit']").ClickAsync();

        await Expect(Page).ToHaveURLAsync(new Regex("/dev/clictopay/pay/"));
        await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Payer" }).ClickAsync();

        await Expect(Page).ToHaveURLAsync(new Regex("/fr/reserver/confirmation/LOC-[0-9]{4}-[0-9]{6}$"));
        await Expect(Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Level = 1 })).ToHaveTextAsync("Demande reçue");
        await Expect(Page.Locator("[data-testid='guarantee-state']")).ToHaveAttributeAsync("data-state", "Held");
        await ShouldBeAccessibleOnAPhoneAsync();
    }

    [Fact]
    public async Task The_english_funnel_speaks_english_and_switches_language_on_the_same_step()
    {
        await Page.GotoAsync($"/en/book?lieu=TUN&depart={LocalDate(64)}&retour={LocalDate(67)}");
        await Page.Locator("li.offer:has(.offer__code:text-is('ECMR')) form.offer__choose button").ClickAsync();

        await Expect(Page).ToHaveURLAsync(new Regex("/en/book/[A-Za-z0-9_-]{43}/options$"));
        await Expect(Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Level = 1 })).ToHaveTextAsync("Extras and price");
        await Expect(Page.GetByText("Step 1 of 3")).ToBeVisibleAsync();
        await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Continue to the drivers" }).ClickAsync();
        await Expect(Page).ToHaveURLAsync(new Regex("/en/book/[A-Za-z0-9_-]{43}/drivers$"));
        await Expect(Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Level = 1 })).ToHaveTextAsync("Who will drive?");
        await ShouldBeAccessibleOnAPhoneAsync();

        await Page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = "Français" }).ClickAsync();
        await Expect(Page).ToHaveURLAsync(new Regex("/fr/reserver/[A-Za-z0-9_-]{43}/conducteurs$"));
        await Expect(Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Level = 1 })).ToHaveTextAsync("Qui conduira ?");
    }

    [Fact]
    public async Task The_payment_step_can_be_completed_with_the_keyboard_only()
    {
        var suffix = Unique();
        await Page.GotoAsync($"/fr/reserver?lieu=TUN&depart={LocalDate(68)}&retour={LocalDate(71)}");
        await Page.Locator("li.offer:has(.offer__code:text-is('ECMR')) form.offer__choose button").ClickAsync();
        await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Continuer vers les conducteurs" }).ClickAsync();
        await FillDriverAsync(suffix, $"e2e.{suffix.ToLowerInvariant()}@example.test");
        await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Continuer vers le paiement" }).ClickAsync();
        await Expect(Page).ToHaveURLAsync(new Regex("/paiement$"));

        await TabUntilFocusedAsync("#terms");
        await Page.Keyboard.PressAsync("Space");
        await Expect(Page.Locator("#terms")).ToBeCheckedAsync();
        await TabUntilFocusedAsync("[data-testid='funnel-submit']");
        await Page.Keyboard.PressAsync("Enter");

        await Expect(Page).ToHaveURLAsync(new Regex("/dev/clictopay/pay/"));
    }

    [Fact]
    public async Task My_booking_opens_from_the_link_of_the_email_and_takes_the_photos()
    {
        var request = await SubmitRequestAsync(72, 75);
        await Page.GotoAsync("/fr/ma-reservation");
        await ShouldBeAccessibleOnAPhoneAsync();
        await Page.GetByLabel("Adresse e-mail").FillAsync(request.Email);
        await Page.GetByLabel("Numéro de réservation").FillAsync(request.Number);
        await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Recevoir mon lien d'accès" }).ClickAsync();

        await Expect(Page.Locator("[data-testid='access-sent']")).ToBeVisibleAsync();
        await ShouldBeAccessibleOnAPhoneAsync();
        var email = await TestEmails.WaitForAsync(
            Host.Factory.EmailDirectory,
            e => e.To.Contains(request.Email, StringComparison.Ordinal) && e.Subject.Contains(request.Number, StringComparison.Ordinal),
            TestContext.Current.CancellationToken);
        var link = TestEmails.Links(email).Single(l => l.Contains("/fr/ma-reservation/acces/", StringComparison.Ordinal));
        link.ShouldStartWith(Host.BaseAddress.GetLeftPart(UriPartial.Authority));

        await Page.GotoAsync(link);
        await ShouldBeAccessibleOnAPhoneAsync();
        await Page.Locator("[data-testid='open-booking']").ClickAsync();

        await Expect(Page).ToHaveURLAsync(new Regex("/fr/ma-reservation/dossier$"));
        await Expect(Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Level = 1 })).ToHaveTextAsync($"Réservation {request.Number}");
        await Expect(Page.Locator("[data-testid='booking-status']")).ToHaveTextAsync("En cours de validation");
        await Page.Locator("#photo-main-license-back").SetInputFilesAsync(new FilePayload { Name = "permis-verso.png", MimeType = "image/png", Buffer = Png });
        await Expect(Page.Locator("#photo-main-license-back-status")).ToHaveTextAsync("Photo reçue.");
        await ShouldBeAccessibleOnAPhoneAsync();

        await Page.Locator("[data-testid='cancel-link']").ClickAsync();
        await Expect(Page.Locator("[data-testid='cancellation-fee']")).ToHaveTextAsync("Aucuns frais");
        await ShouldBeAccessibleOnAPhoneAsync();
    }

    private async Task FillDriverAsync(string suffix, string email)
    {
        await Page.Locator("#main-first-name").FillAsync("Amel");
        await Page.Locator("#main-last-name").FillAsync($"E2E{suffix}");
        await Page.Locator("#main-nationality").SelectOptionAsync("FR");
        await Page.Locator("#main-birth-date").FillAsync("1984-03-14");
        await Page.Locator("#main-phone").FillAsync($"+336{Random.Shared.Next(10_000_000, 99_999_999)}");
        await Page.Locator("#main-email").FillAsync(email);
        await Page.Locator("#main-license-number").FillAsync($"L{suffix}");
        await Page.Locator("#main-license-country").SelectOptionAsync("FR");
        await Page.Locator("#main-license-issued-on").FillAsync("2004-06-01");
        await Page.Locator("#main-identity-number").FillAsync($"P{suffix}");
        await Page.Locator("#main-identity-expires-on").FillAsync("2035-01-31");
        await Page.Locator("#consent-documents").CheckAsync();
    }

    /// <summary>A request of the demo agency sent through the funnel services (the access tests start from a booking).</summary>
    private async Task<(string Number, string Email)> SubmitRequestAsync(int pickupDays, int returnDays)
    {
        var suffix = Unique();
        var email = $"e2e.access.{suffix.ToLowerInvariant()}@example.test";
        await using var scope = Host.Factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ScopedUserContext>().Initialize(UserContextSnapshot.System("system:public-funnel"));
        var referential = scope.ServiceProvider.GetRequiredService<IReferentialQueries>();
        var agency = (await referential.GetAgenciesAsync()).Single(a => a.Name == "Agence Démo Tunis");
        var office = (await referential.GetLocationsAsync(agency.Id)).Single(l => l.Code == "TUN");
        var category = (await scope.ServiceProvider.GetRequiredService<IFleetQueries>().FindCategoryByCodeAsync(agency.Id, "ECMR")).ShouldNotBeNull();
        var funnel = scope.ServiceProvider.GetRequiredService<FunnelService>();
        var pickup = TunisTime.FromLocal(TunisTime.Today(TimeProvider.System).AddDays(pickupDays), new TimeOnly(10, 0));
        var dropOff = TunisTime.FromLocal(TunisTime.Today(TimeProvider.System).AddDays(returnDays), new TimeOnly(10, 0));
        var started = await funnel.StartAsync(new FunnelStartRequest(agency.Id, "fr", SalesChannel.Web, null, category.Id, "ECMR", office.Id, office.Id, pickup, dropOff, "?lieu=TUN", null));
        started.IsSuccess.ShouldBeTrue(started.IsFailure ? started.Error.ToString() : null);
        var driver = new FunnelDriverInput(
            "Sami", $"Acces{suffix}", "FR", "1980-02-02", $"+336{Random.Shared.Next(10_000_000, 99_999_999)}", email,
            $"L{suffix}", "2001-01-01", null, "FR", true, $"P{suffix}", null, "2034-12-31");
        var saved = await funnel.SaveDriversAsync(started.Value.Token, new FunnelDriversInput(driver, false, null, true, false, false));
        saved.Value.IsValid.ShouldBeTrue(string.Join(", ", saved.Value.Errors.Select(e => e.Code)));
        var submitted = await funnel.SubmitAsync(started.Value.Token, termsAccepted: true, Host.BaseAddress.ToString(), "fr");
        submitted.IsSuccess.ShouldBeTrue(submitted.IsFailure ? submitted.Error.ToString() : null);
        return (submitted.Value.Number, email);
    }

    private async Task ShouldBeAccessibleOnAPhoneAsync()
    {
        var overflow = await Page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth > window.innerWidth");
        overflow.ShouldBeFalse($"{Page.Url} scrolls horizontally at 390 px.");
        var result = await Page.RunAxe();
        var blocking = result.Violations
            .Where(v => v.Impact is "critical" or "serious")
            .Select(v => $"{v.Id} ({v.Impact}): {v.Help} [{string.Join(", ", v.Nodes.Select(n => n.Target?.ToString()))}]")
            .ToList();
        blocking.ShouldBeEmpty($"{Page.Url}");
    }

    /// <summary>Presses Tab until <paramref name="selector"/> has the focus (at most 200 times: the drivers form is long).</summary>
    private async Task TabUntilFocusedAsync(string selector)
    {
        for (var press = 0; press < 200; press++)
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
