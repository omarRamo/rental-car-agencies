using System.Text.RegularExpressions;
using CarRental.Application.Common.Security;
using CarRental.Application.Identity.Contracts;
using CarRental.Application.Payments.Contracts;
using CarRental.Application.Payments.Online;
using CarRental.Application.Referential.Contracts;
using CarRental.Domain.Common;
using CarRental.Domain.Payments;
using CarRental.Infrastructure.Identity;
using CarRental.Infrastructure.Persistence;
using CarRental.Testing;
using Deque.AxeCore.Playwright;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace CarRental.E2E.Tests.Payments;

/// <summary>
/// Clic to Pay end to end with the simulated gateway: a customer starts a pre-authorization (development page, like the
/// booking funnel will), pays on the payment page of the gateway (390 px) and comes back through the return address to
/// the outcome confirmed by the server; a manager finds the payment pre-authorized in /admin/payments, opens it and
/// captures less than the pre-authorized amount. An agent creates a payment link from the panel of a booking and asks
/// for an online refund above the approval threshold, which a manager approves. No critical or serious accessibility
/// violation on the way.
/// </summary>
public sealed partial class OnlinePaymentsBackOfficeTests(E2EHost host) : BrowserTest(host)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_pre_authorization_paid_on_the_payment_page_is_seen_and_captured_in_the_back_office()
    {
        // The customer, on a phone.
        await Page.GotoAsync("/dev/clictopay/start");
        await Page.GetByLabel("Montant (TND)").FillAsync("150.000");
        await AssertAccessibleAsync(Page);
        await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Démarrer le paiement" }).ClickAsync();

        await Expect(Page).ToHaveURLAsync(PaymentPage());
        await Expect(Page.GetByTestId("fake-amount")).ToHaveTextAsync(Amount("150,000"));
        var orderNumber = (await Page.GetByTestId("fake-order-number").TextContentAsync())!.Trim();
        await AssertAccessibleAsync(Page);
        await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Payer", Exact = true }).ClickAsync();

        await Expect(Page).ToHaveURLAsync(ResultPage());
        await Expect(Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "Paiement autorisé" })).ToBeVisibleAsync();
        await Expect(Page.GetByTestId("payment-result")).ToHaveAttributeAsync("data-status", "PreAuthorized");
        await AssertAccessibleAsync(Page);
        var paymentId = Guid.ParseExact(orderNumber, "N");

        // The manager, on a tablet.
        await Page.SetViewportSizeAsync(1280, 800);
        await SignInAsync(Page, await NewStaffAsync(Roles.Manager), "/admin/payments");
        await WaitUntilReadyAsync(Page);
        await Page.GetByRole(AriaRole.Tab, new PageGetByRoleOptions { Name = "Paiements en ligne" }).ClickAsync();
        var open = Page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = $"Détail du paiement en ligne {orderNumber[^8..].ToUpperInvariant()}" });
        await Expect(open).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30_000 });
        var row = Page.Locator("tr", new PageLocatorOptions { Has = open });
        await Expect(row).ToContainTextAsync("Pré-autorisé");
        await Expect(row).ToContainTextAsync(Amount("150,000"));
        await Expect(row).ToContainTextAsync("Pré-autorisation");
        await AssertAccessibleAsync(Page);
        await open.ClickAsync();

        await Expect(Page).ToHaveURLAsync(new Regex($"/admin/payments/online/{paymentId}$"));
        await WaitUntilReadyAsync(Page);
        var summary = Page.GetByTestId("online-summary");
        await Expect(summary).ToContainTextAsync(Amount("150,000"));
        await Expect(summary).ToContainTextAsync(orderNumber);
        await Expect(Page.GetByTestId("online-calls")).ToContainTextAsync("Lecture du statut");
        await AssertAccessibleAsync(Page);
        await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Capturer", Exact = true }).ClickAsync();
        var dialog = Page.GetByRole(AriaRole.Dialog);
        await Expect(dialog).ToBeVisibleAsync();
        await dialog.GetByLabel("Montant à capturer").FillAsync("120");
        await AssertAccessibleAsync(Page);
        await dialog.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Capturer", Exact = true }).ClickAsync();

        await Expect(dialog).ToHaveCountAsync(0, new LocatorAssertionsToHaveCountOptions { Timeout = 30_000 });
        await Expect(Page.GetByTestId("online-captured")).ToHaveTextAsync(Amount("120,000"), new LocatorAssertionsToHaveTextOptions { Timeout = 30_000 });
        await Expect(Page.GetByTestId("online-calls")).ToContainTextAsync("Capture");
        await AssertAccessibleAsync(Page);

        var payment = await PaymentAsync(paymentId);
        payment.Status.ShouldBe(PaymentStatus.Captured);
        payment.Amount.ShouldBe(Money.Tnd(150m));
        payment.CapturedAmount.ShouldBe(Money.Tnd(120m));
        payment.Method.ShouldBe(PaymentMethod.ClicToPay);
    }

    [Fact]
    public async Task An_agent_creates_a_payment_link_and_asks_for_a_refund_that_a_manager_approves()
    {
        await Page.SetViewportSizeAsync(1280, 800);
        var bookingId = Guid.CreateVersion7();
        var paymentId = await PaidOnlineAsync(bookingId, 300m);
        await SignInAsync(Page, await NewStaffAsync(Roles.Agent), $"/admin/payments/bookings/{bookingId}");
        var panel = Page.Locator("[data-testid=booking-payments][data-ready=true]");
        await Expect(panel).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30_000 });

        // A payment link of 80 TND for the balance.
        await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Lien de paiement", Exact = true }).ClickAsync();
        var linkDialog = Page.GetByRole(AriaRole.Dialog);
        await linkDialog.GetByLabel("Montant", new LocatorGetByLabelOptions { Exact = true }).FillAsync("80");
        await AssertAccessibleAsync(Page);
        await linkDialog.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Créer le lien" }).ClickAsync();
        await Expect(linkDialog.GetByLabel("Adresse du lien")).ToHaveValueAsync(new Regex("/dev/clictopay/pay/[0-9a-f-]{36}$"), new LocatorAssertionsToHaveValueOptions { Timeout = 30_000 });
        await AssertAccessibleAsync(Page);
        await linkDialog.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Terminer" }).ClickAsync();
        await Expect(linkDialog).ToHaveCountAsync(0);
        var linkRow = panel.Locator("tr", new LocatorLocatorOptions { HasText = "En attente" });
        await Expect(linkRow).ToContainTextAsync(Amount("80,000"), new LocatorAssertionsToContainTextOptions { Timeout = 30_000 });
        await Expect(linkRow).ToContainTextAsync("Clic to Pay");

        // An online refund above the approval threshold (100 TND): it waits for a manager.
        await Page.GotoAsync($"/admin/payments/online/{paymentId}");
        await WaitUntilReadyAsync(Page);
        await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Rembourser en ligne" }).ClickAsync();
        var refundDialog = Page.GetByRole(AriaRole.Dialog);
        await refundDialog.GetByLabel("Montant", new LocatorGetByLabelOptions { Exact = true }).FillAsync("150");
        await refundDialog.GetByLabel("Motif du remboursement").FillAsync("Annulation client J-5");
        await AssertAccessibleAsync(Page);
        await refundDialog.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Rembourser", Exact = true }).ClickAsync();
        await Expect(refundDialog).ToHaveCountAsync(0, new LocatorAssertionsToHaveCountOptions { Timeout = 30_000 });
        await Expect(Page.Locator("tr", new PageLocatorOptions { HasText = "Annulation client J-5" })).ToContainTextAsync("À valider par un gérant", new LocatorAssertionsToContainTextOptions { Timeout = 30_000 });
        await Expect(Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Valider", Exact = true })).ToHaveCountAsync(0);
        await AssertAccessibleAsync(Page);

        // The manager approves it on another device.
        await using var managerContext = await Host.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = Host.BaseAddress.ToString(),
            ViewportSize = new ViewportSize { Width = 1280, Height = 800 },
            Locale = "fr-FR",
        });
        var managerPage = await managerContext.NewPageAsync();
        await SignInAsync(managerPage, await NewStaffAsync(Roles.Manager), $"/admin/payments/online/{paymentId}");
        await WaitUntilReadyAsync(managerPage);
        await managerPage.GetByRole(AriaRole.Button, new PageGetByRoleOptions { NameRegex = new Regex("^Valider le remboursement de 150,000") }).ClickAsync();
        var refundRow = managerPage.Locator("tr", new PageLocatorOptions { HasText = "Annulation client J-5" });
        await Expect(refundRow).ToContainTextAsync("Remboursé", new LocatorAssertionsToContainTextOptions { Timeout = 30_000 });
        await Expect(refundRow).ToContainTextAsync("Validé par");
        await AssertAccessibleAsync(managerPage);

        var payment = await PaymentAsync(paymentId);
        payment.RefundedAmount.ShouldBe(Money.Tnd(150m));
        payment.PendingRefundAmount.ShouldBe(Money.Zero);
        payment.Status.ShouldBe(PaymentStatus.Captured);
    }

    [GeneratedRegex("/dev/clictopay/pay/[0-9a-f-]{36}$")]
    private static partial Regex PaymentPage();

    [GeneratedRegex("/fr/paiement/[0-9a-f-]{36}$")]
    private static partial Regex ResultPage();

    /// <summary>An amount in TND (the group separator of the culture may be a narrow no-break space).</summary>
    private static Regex Amount(string value) => new($"{Regex.Escape(value)}\\sTND");

    private static Task WaitUntilReadyAsync(IPage page) =>
        Expect(page.Locator("h1[data-ready=true]")).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30_000 });

    private static async Task AssertAccessibleAsync(IPage page)
    {
        // Toasts fade in and out (transient blended colours): close them, the page content is what is checked.
        foreach (var close in await page.Locator(".mud-snackbar button").AllAsync())
        {
            await close.ClickAsync();
        }

        await Expect(page.Locator(".mud-snackbar")).ToHaveCountAsync(0);
        var result = await page.RunAxe();
        var blocking = result.Violations
            .Where(v => v.Impact is "critical" or "serious")
            .Select(v => $"{v.Id} ({v.Impact}): {v.Help} [{string.Join(", ", v.Nodes.Select(n => $"{n.Target} {(n.Html.Length > 300 ? n.Html[..300] : n.Html)}"))}]")
            .ToList();
        blocking.ShouldBeEmpty();
    }

    private static async Task SignInAsync(IPage page, string email, string returnUrl)
    {
        await page.GotoAsync($"/account/login?ReturnUrl={Uri.EscapeDataString(returnUrl)}");
        await page.Locator("#email").FillAsync(email);
        await page.Locator("#password").FillAsync(TestAccounts.Password);
        await page.Locator("button[type=submit]").ClickAsync();
        await page.Locator("#code").FillAsync(Totp.Compute(TestAccounts.AuthenticatorKey));
        await page.Locator("button[type=submit]").ClickAsync();
        await Expect(page).ToHaveURLAsync(new Regex(Regex.Escape(returnUrl) + "$"));
    }

    /// <summary>A staff account of its own (no concurrent sign-in updating the shared demo users).</summary>
    private async Task<string> NewStaffAsync(string role)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var email = $"{role.ToLowerInvariant()}-online-{suffix}@tests.local";
        await using var scope = Host.Factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ScopedUserContext>().Initialize(UserContextSnapshot.System("system:e2e"));
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = new ApplicationUser
        {
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            DisplayName = $"{role} en ligne {suffix}",
            LockoutEnabled = true,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };
        (await users.CreateAsync(user, TestAccounts.Password)).Succeeded.ShouldBeTrue();
        (await users.AddToRoleAsync(user, role)).Succeeded.ShouldBeTrue();
        var keys = (IUserAuthenticatorKeyStore<ApplicationUser>)scope.ServiceProvider.GetRequiredService<IUserStore<ApplicationUser>>();
        await keys.SetAuthenticatorKeyAsync(user, TestAccounts.AuthenticatorKey, Ct);
        (await users.SetTwoFactorEnabledAsync(user, true)).Succeeded.ShouldBeTrue();
        return email;
    }

    /// <summary>A one-step online payment of the booking, paid on the simulated gateway and confirmed by the return.</summary>
    private async Task<Guid> PaidOnlineAsync(Guid bookingId, decimal amount)
    {
        await using var scope = Host.Factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ScopedUserContext>().Initialize(UserContextSnapshot.Anonymous("198.51.100.40"));
        var agencyId = (await scope.ServiceProvider.GetRequiredService<IReferentialQueries>().GetAgenciesAsync(Ct)).First(a => a.IsActive).Id;
        var online = scope.ServiceProvider.GetRequiredService<IOnlinePayments>();
        var started = (await online.StartPaymentAsync(new OnlinePaymentRequest(bookingId, agencyId, amount, "fr", "/fr/ok", "/fr/ko"), Ct)).Value;
        var fake = Host.Factory.Services.GetRequiredService<FakeClicToPay>();
        fake.Pay(fake.FindOrderByNumber(started.OrderNumber)!.GatewayOrderId).IsSuccess.ShouldBeTrue();
        (await online.HandleReturnAsync(new OnlinePaymentReturnRequest(started.OrderNumber, null, false), Ct)).Value.Status.ShouldBe(PaymentStatusInfo.Captured);
        return started.PaymentId;
    }

    private async Task<Payment> PaymentAsync(Guid paymentId)
    {
        await using var scope = Host.Factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<CarRentalDbContext>().Set<Payment>().AsNoTracking().SingleAsync(p => p.Id == paymentId, Ct);
    }
}
