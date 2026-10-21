using System.Text.RegularExpressions;
using CarRental.Application.Cash.Contracts;
using CarRental.Application.Common.Security;
using CarRental.Application.Identity.Contracts;
using CarRental.Domain.Cash;
using CarRental.Domain.Common;
using CarRental.E2E.Tests.Support;
using CarRental.Infrastructure.Identity;
using CarRental.Infrastructure.Persistence;
using CarRental.Testing;
using Deque.AxeCore.Playwright;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace CarRental.E2E.Tests.Cash;

/// <summary>
/// Cash desk on a 10-inch tablet (landscape): an agent opens the cash session, records a cash payment from the payments
/// panel of a booking, then closes the session with a variance and its justification; a manager reviews every session
/// and locks a closed one. No critical or serious accessibility violation on the way.
/// </summary>
public sealed partial class CashBackOfficeTests(E2EHost host) : BrowserTest(host)
{
    protected override ViewportSize Viewport => new() { Width = 1280, Height = 800 };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_agent_opens_the_cash_session_records_a_cash_payment_and_closes_with_a_justified_variance()
    {
        var agent = await NewStaffAsync(Roles.Agent);
        await SignInAsync(agent.Email, "/admin/cash");
        await WaitUntilReadyAsync();

        await Page.GetByLabel("Fonds de caisse").FillAsync("100");
        await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Ouvrir ma caisse" }).ClickAsync();
        await Expect(Page.GetByTestId("expected-revenue")).ToHaveTextAsync("100,000 TND", new LocatorAssertionsToHaveTextOptions { Timeout = 30_000 });
        await AssertAccessibleAsync();

        // The payments panel of a booking (the booking page of package 1E embeds the same component).
        await Page.GotoAsync($"/admin/payments/bookings/{Guid.CreateVersion7()}");
        await Expect(Page.Locator("[data-testid=booking-payments][data-ready=true]")).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30_000 });
        await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Encaisser", Exact = true }).ClickAsync();
        var dialog = Page.GetByRole(AriaRole.Dialog);
        await Expect(dialog).ToBeVisibleAsync();
        await Expect(dialog.GetByText("Les espèces sont enregistrées dans votre caisse ouverte.")).ToBeVisibleAsync();
        await dialog.GetByLabel("Montant").FillAsync("250");
        await AssertAccessibleAsync();
        await dialog.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Encaisser", Exact = true }).ClickAsync();

        await Expect(dialog).ToHaveCountAsync(0);
        var payment = Page.Locator("[data-testid=booking-payments] tr", new PageLocatorOptions { HasText = "Espèces" });
        await Expect(payment).ToContainTextAsync("250,000 TND");
        await Expect(payment).ToContainTextAsync("Encaissé");
        await Expect(Page.GetByTestId("net-paid")).ToHaveTextAsync("250,000 TND");
        await AssertAccessibleAsync();

        // Back to the cash desk: the payment is a movement of the session, the expected cash includes it.
        await Page.GotoAsync("/admin/cash");
        await WaitUntilReadyAsync();
        await Expect(Page.GetByTestId("expected-revenue")).ToHaveTextAsync("350,000 TND");
        await Expect(Page.Locator("tr", new PageLocatorOptions { HasText = "Paiement reçu" })).ToContainTextAsync("+250,000 TND");

        await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Clôturer la caisse" }).ClickAsync();
        var closing = Page.GetByRole(AriaRole.Dialog);
        await closing.GetByLabel("Espèces comptées (recette)").FillAsync("345");
        await Expect(closing.GetByTestId("close-variances")).ToContainTextAsync(MinusFive());
        var submit = closing.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Clôturer", Exact = true });
        await Expect(submit).ToBeDisabledAsync();
        await closing.GetByLabel("Justification de l'écart").FillAsync("Erreur de rendu de monnaie");
        await Expect(submit).ToBeEnabledAsync();
        await AssertAccessibleAsync();
        await submit.ClickAsync();

        await Expect(closing).ToHaveCountAsync(0);
        var closed = Page.GetByRole(AriaRole.Status).Filter(new LocatorFilterOptions { HasText = "Votre caisse du jour est clôturée" });
        await Expect(closed).ToContainTextAsync(MinusFive(), new LocatorAssertionsToContainTextOptions { Timeout = 30_000 });
        await AssertAccessibleAsync();

        var session = await SessionOfAsync(agent.Id);
        session.Status.ShouldBe(CashSessionStatus.Closed);
        session.ExpectedRevenueCash.ShouldBe(Money.Tnd(350m));
        session.CountedRevenueCash.ShouldBe(Money.Tnd(345m));
        session.RevenueVariance.ShouldBe(Money.Tnd(-5m));
        session.VarianceJustification.ShouldBe("Erreur de rendu de monnaie");
        session.Movements.ShouldHaveSingleItem().Kind.ShouldBe(CashMovementKind.PaymentReceived);
    }

    [Fact]
    public async Task A_manager_reviews_every_session_and_locks_a_closed_one()
    {
        var agent = await NewStaffAsync(Roles.Agent);
        var sessionId = await ClosedSessionAsync(agent);
        var manager = await NewStaffAsync(Roles.Manager);
        await SignInAsync(manager.Email, "/admin/cash");
        await WaitUntilReadyAsync();

        await Page.GetByRole(AriaRole.Tab, new PageGetByRoleOptions { Name = "Toutes les caisses" }).ClickAsync();
        var row = Page.Locator("tr", new PageLocatorOptions { HasText = agent.DisplayName });
        await Expect(row).ToContainTextAsync("Clôturée", new LocatorAssertionsToContainTextOptions { Timeout = 30_000 });
        await Expect(row).ToContainTextAsync(MinusTwo());
        await AssertAccessibleAsync();
        await row.GetByRole(AriaRole.Link, new LocatorGetByRoleOptions { Name = "Voir" }).ClickAsync();

        await Expect(Page).ToHaveURLAsync(new Regex($"/admin/cash/sessions/{sessionId}$"));
        await WaitUntilReadyAsync();
        await Expect(Page.GetByTestId("variance-justification")).ToContainTextAsync("Pièce de 2 dinars égarée");
        await AssertAccessibleAsync();
        await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Verrouiller", Exact = true }).ClickAsync();
        await Page.GetByRole(AriaRole.Dialog).GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Verrouiller", Exact = true }).ClickAsync();

        await Expect(Page.GetByTestId("cash-session")).ToContainTextAsync("Verrouillée", new LocatorAssertionsToContainTextOptions { Timeout = 30_000 });
        await Expect(Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Verrouiller", Exact = true })).ToHaveCountAsync(0);
        await AssertAccessibleAsync();
        var session = await SessionOfAsync(agent.Id);
        session.Status.ShouldBe(CashSessionStatus.Locked);
        session.LockedByName.ShouldBe(manager.Email);
    }

    [Fact]
    public async Task An_agent_takes_a_card_imprint_and_retains_part_of_it_with_a_justification()
    {
        var agent = await NewStaffAsync(Roles.Agent);
        var bookingId = Guid.CreateVersion7();
        await SignInAsync(agent.Email, $"/admin/payments/bookings/{bookingId}");
        var panel = Page.Locator("[data-testid=booking-payments][data-ready=true]");
        await Expect(panel).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30_000 });

        await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Prendre une caution" }).ClickAsync();
        var taking = Page.GetByRole(AriaRole.Dialog);
        await taking.GetByLabel("Montant").FillAsync("800");
        await taking.GetByLabel("N° d'autorisation de l'empreinte").FillAsync("AUT-778812");
        await AssertAccessibleAsync();
        await taking.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Prendre la caution" }).ClickAsync();

        await Expect(taking).ToHaveCountAsync(0);
        var deposit = panel.Locator("tr", new LocatorLocatorOptions { HasText = "Empreinte de carte" });
        await Expect(deposit).ToContainTextAsync("Bloquée");
        await Expect(panel.GetByTestId("deposit-totals")).ToContainTextAsync("800,000 TND");

        await deposit.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Retenir" }).ClickAsync();
        var retaining = Page.GetByRole(AriaRole.Dialog);
        await retaining.GetByLabel("Montant retenu").FillAsync("150");
        await retaining.GetByLabel("Justification de la retenue").FillAsync("Rayure portière avant droite");
        await Expect(retaining.GetByRole(AriaRole.Status)).ToContainTextAsync("650,000 TND");
        await AssertAccessibleAsync();
        await retaining.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Retenir", Exact = true }).ClickAsync();

        await Expect(retaining).ToHaveCountAsync(0);
        await Expect(deposit).ToContainTextAsync("Retenue partielle");
        await Expect(deposit).ToContainTextAsync("150,000 TND");
        await Expect(panel.GetByTestId("deposit-totals")).ToContainTextAsync("0,000 TND");
        await AssertAccessibleAsync();

        // The deposits list of the payments screen finds it by booking.
        await Page.GotoAsync("/admin/payments");
        await WaitUntilReadyAsync();
        await Page.GetByRole(AriaRole.Tab, new PageGetByRoleOptions { Name = "Cautions" }).ClickAsync();
        await Page.GetByLabel("Identifiant du dossier", new PageGetByLabelOptions { Exact = true }).FillAsync(bookingId.ToString());
        await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Rechercher" }).ClickAsync();
        var listed = Page.Locator("tr", new PageLocatorOptions { HasText = "Rayure portière avant droite" });
        await Expect(listed).ToContainTextAsync("Retenue partielle", new LocatorAssertionsToContainTextOptions { Timeout = 30_000 });
        await Expect(Page.GetByRole(AriaRole.Row)).ToHaveCountAsync(2);
        await AssertAccessibleAsync();
    }

    /// <summary>A variance of −5,000 TND (the minus sign of the culture may be a hyphen or U+2212).</summary>
    [GeneratedRegex("[-−]5,000 TND")]
    private static partial Regex MinusFive();

    [GeneratedRegex("[-−]2,000 TND")]
    private static partial Regex MinusTwo();

    /// <summary>The page heading is marked ready once the page is interactive and loaded.</summary>
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
        await BackOfficeSession.WaitForTransitionsAsync(Page);
        var result = await Page.RunAxe();
        var blocking = result.Violations
            .Where(v => v.Impact is "critical" or "serious")
            .Select(v => $"{v.Id} ({v.Impact}): {v.Help} [{string.Join(", ", v.Nodes.Select(n => $"{n.Target} {(n.Html.Length > 300 ? n.Html[..300] : n.Html)}"))}]")
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

    /// <summary>A staff account of its own (no concurrent sign-in updating the shared demo users, no seeded session).</summary>
    private async Task<Staff> NewStaffAsync(string role)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var email = $"{role.ToLowerInvariant()}-cash-{suffix}@tests.local";
        await using var scope = Host.Factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ScopedUserContext>().Initialize(UserContextSnapshot.System("system:e2e"));
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = new ApplicationUser
        {
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            DisplayName = $"{role} caisse {suffix}",
            LockoutEnabled = true,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };
        (await users.CreateAsync(user, TestAccounts.Password)).Succeeded.ShouldBeTrue();
        (await users.AddToRoleAsync(user, role)).Succeeded.ShouldBeTrue();
        var keys = (IUserAuthenticatorKeyStore<ApplicationUser>)scope.ServiceProvider.GetRequiredService<IUserStore<ApplicationUser>>();
        await keys.SetAuthenticatorKeyAsync(user, TestAccounts.AuthenticatorKey, Ct);
        (await users.SetTwoFactorEnabledAsync(user, true)).Succeeded.ShouldBeTrue();
        return new Staff(user.Id, email, user.DisplayName, role);
    }

    /// <summary>A session of <paramref name="agent"/> opened with 50.000 TND, closed with 48.000 TND counted.</summary>
    private async Task<Guid> ClosedSessionAsync(Staff agent)
    {
        await using var scope = Host.Factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ScopedUserContext>()
            .Initialize(UserContextSnapshot.ForUser(agent.Id, agent.Email, RolePresets.PermissionsOf(agent.Role)));
        var sessions = scope.ServiceProvider.GetRequiredService<ICashSessions>();
        var agencyId = (await scope.ServiceProvider.GetRequiredService<CarRental.Application.Referential.Contracts.IReferentialQueries>().GetAgenciesAsync(Ct))
            .First(a => a.IsActive).Id;
        var opened = await sessions.OpenAsync(new OpenCashSessionRequest(agencyId, 50m), Ct);
        opened.IsSuccess.ShouldBeTrue(opened.IsFailure ? opened.Error.ToString() : null);
        var closed = await sessions.CloseAsync(opened.Value, new CloseCashSessionRequest(48m, 0m, "Pièce de 2 dinars égarée"), Ct);
        closed.IsSuccess.ShouldBeTrue(closed.IsFailure ? closed.Error.ToString() : null);
        return opened.Value;
    }

    private async Task<CashSession> SessionOfAsync(Guid cashierId)
    {
        await using var scope = Host.Factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<CarRentalDbContext>().Set<CashSession>()
            .AsNoTracking()
            .SingleAsync(s => s.CashierId == cashierId, Ct);
    }

    private sealed record Staff(Guid Id, string Email, string DisplayName, string Role);
}
