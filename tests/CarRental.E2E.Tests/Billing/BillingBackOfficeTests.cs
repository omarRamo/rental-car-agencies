using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using CarRental.Application.Billing.Fines;
using CarRental.Application.Common.Persistence;
using CarRental.Application.Common.Security;
using CarRental.Application.Fleet.Contracts;
using CarRental.Application.Identity.Contracts;
using CarRental.Domain.Billing;
using CarRental.Domain.Bookings;
using CarRental.Domain.Common;
using CarRental.Domain.Identity;
using CarRental.Domain.Referential;
using CarRental.E2E.Tests.Bookings.Handover;
using CarRental.E2E.Tests.Support;
using CarRental.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace CarRental.E2E.Tests.Billing;

/// <summary>
/// The billing screens on a 10-inch tablet (1280 × 800), each test on an agency of its own: an accountant issues the
/// invoice of a closed file, downloads its PDF and credits part of it; an agent records a traffic fine, which finds the
/// driver of the rental, and gets the designation form. No critical or serious accessibility violation on the way.
/// </summary>
public sealed partial class BillingBackOfficeTests(E2EHost host) : BrowserTest(host)
{
    protected override ViewportSize Viewport => new() { Width = 1280, Height = 800 };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_accountant_issues_the_invoice_of_a_closed_file_downloads_its_pdf_and_credits_part_of_it()
    {
        var file = await HandoverE2EData.ConfirmedAsync(Host);
        await IdentifyAsync(file.AgencyId);
        await CloseAsync(file.BookingId);
        var accountant = await BackOfficeSession.NewStaffAsync(Host, Roles.Accountant);
        await BackOfficeSession.SignInAsync(Page, accountant, $"/admin/invoices?agency={file.AgencyId}&tab=to-invoice");
        await WaitUntilReadyAsync();

        // The closed file waits in the "to invoice" tab of its agency (automatic issue turned off for it).
        var row = Page.Locator("tr", new PageLocatorOptions { HasText = file.Number });
        await Expect(row).ToBeVisibleAsync();
        await AssertAccessibleAsync();
        await row.GetByTestId("issue-now").ClickAsync();

        await Expect(Page).ToHaveURLAsync(InvoicePage());
        await Expect(Page.GetByTestId("invoice-number")).ToHaveTextAsync(InvoiceTitle());
        await Expect(Page.GetByTestId("invoice-total-due")).ToHaveTextAsync(Amount(file.Total));

        // The PDF is prepared by the outbox: its link appears on the page once stored.
        var pdf = Page.GetByTestId("invoice-pdf");
        await Expect(pdf).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 60_000 });
        await WaitUntilReadyAsync();
        await AssertAccessibleAsync();
        var download = await Context.APIRequest.GetAsync((await pdf.GetAttributeAsync("href")).ShouldNotBeNull());
        download.Status.ShouldBe(200);
        download.Headers["content-type"].ShouldStartWith("application/pdf");
        download.Headers["cache-control"].ShouldContain("private");
        Encoding.ASCII.GetString((await download.BodyAsync())[..5]).ShouldBe("%PDF-");

        // A partial credit note: 50.000 TND of the rental line, with its reason.
        await Page.GetByTestId("credit-open").ClickAsync();
        var dialog = Page.GetByRole(AriaRole.Dialog);
        await Expect(dialog).ToBeVisibleAsync();
        await BackOfficeSession.WaitForOpenDialogAsync(Page);
        await dialog.GetByText("Avoir partiel (montants par ligne)").ClickAsync();
        await dialog.GetByLabel("Montant TTC à créditer pour").First.FillAsync("50");
        await dialog.GetByLabel("Motif de l'avoir").FillAsync("Geste commercial");
        await Expect(dialog.GetByTestId("credit-sum")).ToContainTextAsync("50,000 TND");
        await AssertAccessibleAsync();
        await dialog.GetByTestId("credit-submit").ClickAsync();

        await Expect(dialog).ToHaveCountAsync(0);
        var note = Page.GetByTestId("credit-note");
        await Expect(note).ToContainTextAsync("Partiel");
        await Expect(note).ToContainTextAsync("Geste commercial");
        await Expect(note).ToContainTextAsync(MinusFifty());
        await Expect(Page.GetByTestId("invoice-remaining")).ToHaveTextAsync(Amount(file.Total - Money.Tnd(50m)));
        await Expect(note.GetByRole(AriaRole.Link, new LocatorGetByRoleOptions { Name = "PDF de AV-", Exact = false })).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 60_000 });
        await AssertAccessibleAsync();

        await using var scope = Scope(null);
        var db = scope.ServiceProvider.GetRequiredService<CarRentalDbContext>();
        var invoice = await db.Set<Invoice>().AsNoTracking().IgnoreAutoIncludes().SingleAsync(i => i.BookingId == file.BookingId, Ct);
        var credit = await db.Set<CreditNote>().AsNoTracking().IgnoreAutoIncludes().SingleAsync(c => c.InvoiceId == invoice.Id, Ct);
        (credit.Scope, credit.Reason, credit.TotalInclVat, credit.IssuedBy).ShouldBe((CreditNoteScope.Partial, "Geste commercial", Money.Tnd(-50m), accountant));
    }

    [Fact]
    public async Task An_agent_records_a_fine_that_finds_the_driver_of_the_rental_and_gets_the_designation_form()
    {
        var file = await HandoverE2EData.ConfirmedAsync(Host);
        await HandoverE2EData.DepartAsync(Host, file, DateTimeOffset.UtcNow.AddMinutes(-5));
        string plate;
        await using (var scope = Scope(null))
        {
            plate = (await scope.ServiceProvider.GetRequiredService<IFleetQueries>().GetVehicleAsync(file.VehicleId, Ct)).ShouldNotBeNull().PlateNumber;
        }

        var agent = await BackOfficeSession.NewStaffAsync(Host, Roles.Agent);
        await BackOfficeSession.SignInAsync(Page, agent, $"/admin/fines?agency={file.AgencyId}");
        await WaitUntilReadyAsync();
        await AssertAccessibleAsync();

        await Page.GetByTestId("fine-new").ClickAsync();
        var dialog = Page.GetByRole(AriaRole.Dialog);
        await Expect(dialog).ToBeVisibleAsync();
        await BackOfficeSession.WaitForOpenDialogAsync(Page);
        var ticket = $"PV{RandomNumberGenerator.GetInt32(10_000_000, 99_999_999)}";
        await dialog.GetByLabel("N° de l'avis").FillAsync(ticket);
        await dialog.GetByLabel("Autorité").FillAsync("Police de la circulation");
        await dialog.GetByLabel("Date et heure de l'infraction").FillAsync(TunisTime.ToTunis(DateTimeOffset.UtcNow).ToString("yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture));
        await dialog.GetByLabel("Immatriculation").FillAsync(plate);
        await dialog.GetByLabel("Lieu").FillAsync("Avenue de Paris, Tunis");
        await dialog.GetByLabel("Montant (TND)").FillAsync("60");
        await AssertAccessibleAsync();
        await dialog.GetByTestId("fine-submit").ClickAsync();

        // The fine found the rental that had the vehicle and its main driver.
        await Expect(Page).ToHaveURLAsync(FinePage());
        await WaitUntilReadyAsync();
        await Expect(Page.GetByTestId("fine-booking")).ToHaveTextAsync(file.Number);
        await Expect(Page.GetByTestId("fine-driver")).ToHaveTextAsync(file.DriverName);
        await Expect(Page.GetByTestId("fine-status")).ToHaveTextAsync("Enregistrée");
        await AssertAccessibleAsync();

        // The designation form: a PDF produced on demand, never cached, each production logged.
        var designation = Page.GetByTestId("fine-designation");
        var form = await Context.APIRequest.GetAsync((await designation.GetAttributeAsync("href")).ShouldNotBeNull());
        form.Status.ShouldBe(200);
        form.Headers["content-type"].ShouldStartWith("application/pdf");
        form.Headers["cache-control"].ShouldContain("no-store");
        Encoding.ASCII.GetString((await form.BodyAsync())[..5]).ShouldBe("%PDF-");
        await using (var scope = Scope(null))
        {
            var reason = $"fine:{ticket}";
            var accesses = await scope.ServiceProvider.GetRequiredService<CarRentalDbContext>().Set<DocumentAccessLog>()
                .AsNoTracking()
                .Where(a => a.Access == FineService.DesignationAccess && a.Reason == reason)
                .ToListAsync(Ct);
            accesses.Count.ShouldBe(2, "the identity card and the driving licence of the driver");
            accesses.ShouldAllBe(a => a.UserName == agent);
        }

        await Page.GetByTestId("fine-designated").ClickAsync();
        var confirm = Page.GetByRole(AriaRole.Dialog);
        await Expect(confirm).ToBeVisibleAsync();
        await BackOfficeSession.WaitForOpenDialogAsync(Page);
        await confirm.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Marquer le conducteur désigné" }).ClickAsync();
        await Expect(Page.GetByTestId("fine-status")).ToHaveTextAsync("Conducteur désigné", new LocatorAssertionsToHaveTextOptions { Timeout = 30_000 });
        await AssertAccessibleAsync();
    }

    [GeneratedRegex(@"/admin/invoices/[0-9a-f-]{36}$")]
    private static partial Regex InvoicePage();

    [GeneratedRegex(@"/admin/fines/[0-9a-f-]{36}$")]
    private static partial Regex FinePage();

    [GeneratedRegex(@"^Facture FAC-\d{4}-\d{6}$")]
    private static partial Regex InvoiceTitle();

    /// <summary>−50,000 TND (the minus sign of the culture may be a hyphen or U+2212).</summary>
    [GeneratedRegex("[-−]50,000 TND")]
    private static partial Regex MinusFifty();

    private static string Amount(Money amount) => amount.ToString("N", CultureInfo.GetCultureInfo("fr"));

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
        await BackOfficeSession.AssertNoSeriousAxeViolationAsync(Page);
    }

    /// <summary>Gives the agency its seller identity and turns its automatic issue off (the accountant issues).</summary>
    private async Task IdentifyAsync(Guid agencyId)
    {
        await using var scope = Scope(null);
        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        var agency = await db.Set<Agency>().SingleAsync(a => a.Id == agencyId, Ct);
        var digits = RandomNumberGenerator.GetInt32(1_000_000, 9_999_999);
        agency.UpdateLegalIdentity(new AgencyLegalIdentity($"Location E2E {digits} SARL", $"MF-E2E-{digits}", null, "12 rue des Essais, 1000 Tunis", null)).IsSuccess.ShouldBeTrue();
        db.Set<Setting>().Add(Setting.Create("billing.issue_automatically", "false", agencyId).Value);
        await db.SaveChangesAsync(Ct);
    }

    /// <summary>Marks the file closed (test data only: the invoice of such a file bills its quote).</summary>
    private async Task CloseAsync(Guid bookingId)
    {
        await using var scope = Scope(null);
        await scope.ServiceProvider.GetRequiredService<CarRentalDbContext>().Database
            .ExecuteSqlInterpolatedAsync($"UPDATE bookings.Bookings SET Status = {(int)BookingStatus.Closed} WHERE Id = {bookingId}", Ct);
    }

    private AsyncServiceScope Scope(UserContextSnapshot? actor)
    {
        var scope = Host.Factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ScopedUserContext>().Initialize(actor ?? UserContextSnapshot.System("system:e2e"));
        return scope;
    }
}
