using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CarRental.Application.Common.Persistence;
using CarRental.Application.Common.Security;
using CarRental.Application.Identity.Contracts;
using CarRental.Domain.Accounting;
using CarRental.Domain.Cash;
using CarRental.Domain.Cash.Expenses;
using CarRental.Domain.Common;
using CarRental.Domain.Referential;
using CarRental.E2E.Tests.Support;
using CarRental.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace CarRental.E2E.Tests.Accounting;

/// <summary>
/// The accountant's month on a laptop (1280 × 900, ADR-0063 to ADR-0065): she records an expense with its receipt,
/// justifies the counted difference of a cash session of the month just over, exports its cash journal and closes it —
/// on an agency of her own whose locked session of last month is seeded; no serious or critical accessibility violation.
/// </summary>
public sealed partial class AccountingBackOfficeTests(E2EHost host) : BrowserTest(host)
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52];

    private static readonly LocatorAssertionsToBeVisibleOptions Slow = new() { Timeout = 30_000 };

    protected override ViewportSize Viewport => new() { Width = 1280, Height = 900 };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_accountant_records_an_expense_with_its_receipt_justifies_a_difference_exports_and_closes_the_month()
    {
        var month = AccountingMonth.Of(TunisTime.Today(TimeProvider.System)).Previous();
        var (agencyId, sessionId) = await AgencyWithLockedSessionAsync(month);
        var accountant = await BackOfficeSession.NewStaffAsync(Host, Roles.Accountant);
        await BackOfficeSession.SignInAsync(Page, accountant, $"/admin/expenses?agency={agencyId}");
        await Expect(Page.Locator("h1[data-ready=true]")).ToBeVisibleAsync(Slow);

        // An expense of today paid by transfer, with the photo of its receipt.
        await Page.GetByTestId("expense-new").ClickAsync();
        var dialog = Page.GetByRole(AriaRole.Dialog);
        await Expect(dialog).ToBeVisibleAsync();
        await dialog.Locator("#expense-supplier").FillAsync("Station Agil Lac");
        await dialog.Locator("#expense-incl-vat").FillAsync("119");
        await Expect(dialog.GetByTestId("expense-amounts")).ToContainTextAsync("100,000 TND");
        await dialog.Locator("input[type=file]").SetInputFilesAsync(new FilePayload { Name = "ticket.png", MimeType = "image/png", Buffer = Png });
        await Expect(dialog.GetByText("Fichier choisi : ticket.png")).ToBeVisibleAsync();
        await BackOfficeSession.WaitForOpenDialogAsync(Page);
        await BackOfficeSession.AssertNoSeriousAxeViolationAsync(Page);
        await dialog.GetByTestId("expense-save").ClickAsync();

        await Expect(dialog).ToHaveCountAsync(0);
        var number = $"DEP-{TunisTime.Today(TimeProvider.System).Year:D4}-000001";
        var row = Page.Locator("tr", new PageLocatorOptions { HasText = number });
        await Expect(row).ToContainTextAsync("Station Agil Lac", new LocatorAssertionsToContainTextOptions { Timeout = 30_000 });
        await Expect(row).ToContainTextAsync("119,000 TND");
        var receipt = row.GetByRole(AriaRole.Link, new LocatorGetByRoleOptions { Name = $"Ouvrir le justificatif de la dépense {number}", Exact = true });
        await Expect(receipt).ToBeVisibleAsync();
        var served = await Context.APIRequest.GetAsync((await receipt.GetAttributeAsync("href"))!);
        served.Status.ShouldBe(200);
        served.Headers["content-type"].ShouldBe("image/png");
        await BackOfficeSession.CloseSnackbarsAsync(Page);
        await BackOfficeSession.AssertNoSeriousAxeViolationAsync(Page);

        // Last month: the cash session counted 2,000 TND short, justified.
        await Page.GotoAsync($"/admin/reconciliation?agency={agencyId}");
        await Expect(Page.Locator("h1[data-ready=true]")).ToBeVisibleAsync(Slow);
        var key = $"cash:variance:{sessionId:N}:revenue";
        var difference = Page.GetByTestId($"difference-{key}");
        await Expect(difference).ToContainTextAsync("Non justifié", new LocatorAssertionsToContainTextOptions { Timeout = 30_000 });
        await Expect(difference).ToContainTextAsync(MinusTwo());
        await BackOfficeSession.AssertNoSeriousAxeViolationAsync(Page);
        await Page.GetByTestId($"justify-{key}").ClickAsync();
        var justify = Page.GetByRole(AriaRole.Dialog);
        await justify.Locator("#justification-reason").FillAsync("Pièce de 2 dinars égarée, constatée au comptage");
        await BackOfficeSession.WaitForOpenDialogAsync(Page);
        await BackOfficeSession.AssertNoSeriousAxeViolationAsync(Page);
        await justify.GetByTestId("justification-save").ClickAsync();

        await Expect(justify).ToHaveCountAsync(0);
        await Expect(difference).ToContainTextAsync("Justifié");
        await Expect(difference).ToContainTextAsync("Pièce de 2 dinars égarée, constatée au comptage");
        await BackOfficeSession.CloseSnackbarsAsync(Page);
        await BackOfficeSession.AssertNoSeriousAxeViolationAsync(Page);

        // Its cash journal, exported in the generic CSV.
        await Page.GotoAsync($"/admin/accounting?agency={agencyId}");
        await Expect(Page.Locator("h1[data-ready=true]")).ToBeVisibleAsync(Slow);
        await Expect(Page.GetByTestId("journal-totals")).ToContainTextAsync("CA");
        await BackOfficeSession.AssertNoSeriousAxeViolationAsync(Page);
        var download = await Page.RunAndWaitForDownloadAsync(() => Page.GetByTestId("export-csv-CA").ClickAsync());
        download.SuggestedFilename.ShouldBe($"journaux-CA-{month}.csv");
        var directory = Directory.CreateTempSubdirectory("carrental-e2e-journal-");
        try
        {
            var path = Path.Combine(directory.FullName, download.SuggestedFilename);
            await download.SaveAsAsync(path);
            var rows = (await File.ReadAllTextAsync(path, Encoding.UTF8, Ct)).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
            rows[0].ShouldBe("date;journal;compte;piece;libelle;debit;credit;lettrage");
            rows[1..^1].Select(r => r.Split(';')[2]).ShouldBe(["658", "541"]);
            rows[^1].ShouldBe("TOTAL;;;;;2.000;2.000;");
        }
        finally
        {
            directory.Delete(recursive: true);
        }

        // The closing of the month: nothing left to settle, then closed and frozen.
        await Page.GetByRole(AriaRole.Tab, new PageGetByRoleOptions { Name = "Clôture mensuelle" }).ClickAsync();
        await Expect(Page.GetByText("Aucun point à régler ni à confirmer.")).ToBeVisibleAsync(Slow);
        await BackOfficeSession.AssertNoSeriousAxeViolationAsync(Page);
        await Page.GetByTestId("period-close").ClickAsync();

        await Expect(Page.GetByText("Ce mois est clos : ses journaux sont figés.")).ToBeVisibleAsync(Slow);
        await Expect(Page.GetByTestId("closing-periods").Locator("tr", new LocatorLocatorOptions { HasText = "Clos" })).ToHaveCountAsync(1);
        await BackOfficeSession.CloseSnackbarsAsync(Page);
        await BackOfficeSession.AssertNoSeriousAxeViolationAsync(Page);
        await using var scope = Host.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CarRentalDbContext>();
        var period = await db.Set<AccountingPeriod>().AsNoTracking().SingleAsync(p => p.AgencyId == agencyId, Ct);
        (period.Year, period.Month, period.Status, period.ClosedByName).ShouldBe((month.Year, month.Month, AccountingPeriodStatus.Closed, accountant));
        (await db.Set<Expense>().AsNoTracking().SingleAsync(e => e.AgencyId == agencyId, Ct)).ReceiptContentType.ShouldBe("image/png");
    }

    /// <summary>A variance of −2,000 TND (the minus sign of the culture may be a hyphen or U+2212).</summary>
    [GeneratedRegex("[-−]2,000 TND")]
    private static partial Regex MinusTwo();

    /// <summary>
    /// An agency of its own and a cash session of <paramref name="month"/> (the 10th) opened with 50,000 TND, counted 48,000 TND
    /// with the justification of the cashier and locked — written on the aggregate with its own instants, like the demo history.
    /// </summary>
    private async Task<(Guid AgencyId, Guid SessionId)> AgencyWithLockedSessionAsync(AccountingMonth month)
    {
        await using var scope = Host.Factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ScopedUserContext>().Initialize(UserContextSnapshot.System("system:e2e"));
        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        var suffix = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        // After "Agence Démo Tunis" by name: the other screens of the suite open on the first agency, the demo one.
        var agency = Agency.Register(new AgencyDetails($"Agence Zitouna compta {suffix}", "Tunis")).Value;
        var openedAt = TunisTime.FromLocal(month.FirstDay.AddDays(9), new TimeOnly(9, 0));
        var session = CashSession.Open(agency.Id, Guid.CreateVersion7(), string.Create(CultureInfo.InvariantCulture, $"Caissier {suffix}"), Money.Tnd(50m), openedAt).Value;
        var actor = AuditActor.System("system:e2e");
        session.Close(Money.Tnd(48m), Money.Zero, "Pièce de 2 dinars égarée", actor, openedAt.AddHours(9)).IsSuccess.ShouldBeTrue();
        session.Lock(actor, openedAt.AddHours(10)).IsSuccess.ShouldBeTrue();
        db.Set<Agency>().Add(agency);
        db.Set<CashSession>().Add(session);
        await db.SaveChangesAsync(Ct);
        return (agency.Id, session.Id);
    }
}
