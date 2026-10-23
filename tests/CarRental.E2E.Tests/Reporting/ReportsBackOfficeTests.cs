using System.Text.RegularExpressions;
using CarRental.Application.Common.Security;
using CarRental.Application.Identity.Contracts;
using CarRental.Application.Referential.Contracts;
using CarRental.Application.Reporting.Indicators;
using CarRental.Domain.Common;
using CarRental.Domain.Reporting;
using CarRental.E2E.Tests.Support;
using CarRental.Testing;
using ClosedXML.Excel;
using Deque.AxeCore.Playwright;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace CarRental.E2E.Tests.Reporting;

/// <summary>
/// The reports screen on a laptop (1280 × 900), on the demo agency and its demo month (the snapshots and funnel events of
/// the demo seeding): a manager reads the tiles and the tables, shows the data table of a chart, sorts the profitability
/// table and downloads an Excel export, which opens with the rows of the screen; no serious or critical accessibility
/// violation on the way.
/// </summary>
public sealed class ReportsBackOfficeTests(E2EHost host) : BrowserTest(host)
{
    private const string DemoAgencyName = "Agence Démo Tunis";
    private const string RevPavChart = "Revenu par véhicule disponible, par jour";

    private static readonly LocatorAssertionsToBeVisibleOptions Slow = new() { Timeout = 30_000 };

    protected override ViewportSize Viewport => new() { Width = 1280, Height = 900 };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_manager_reads_the_demo_month_shows_a_chart_table_and_downloads_an_excel_export()
    {
        var agencyId = await DemoAgencyIdAsync();
        var expected = await ExpectedReportAsync(agencyId);
        var manager = await BackOfficeSession.NewStaffAsync(Host, Roles.Manager);
        await BackOfficeSession.SignInAsync(Page, manager, "/admin/reports");
        await Expect(Page.Locator("h1[data-ready=true]")).ToBeVisibleAsync(Slow);

        // The tiles, the manager's main indicator first.
        await Expect(Page.Locator(".report-kpi--hero")).ToContainTextAsync("Revenu par véhicule disponible et par jour");
        await Expect(Page.GetByTestId("kpi-revpav")).ToContainTextAsync(Tnd(expected.Fleet.RevenuePerAvailableDay));
        await Expect(Page.GetByTestId("kpi-utilization")).ToContainTextAsync("%");
        await Expect(Page.GetByTestId("kpi-revenue")).ToContainTextAsync(Tnd(expected.Fleet.Revenue));

        // The tables: one row per vehicle (first page), the channels, the pace and the funnel.
        var vehicleRows = Page.GetByTestId("vehicles-table").Locator("tbody tr");
        await Expect(vehicleRows).ToHaveCountAsync(Math.Min(25, expected.Vehicles.Count));
        await Expect(Page.GetByTestId("channels-table")).ToContainTextAsync("Téléphone");
        await Expect(Page.GetByTestId("pace-table")).ToContainTextAsync("Toutes catégories");
        await Expect(Page.GetByTestId("funnel-table")).ToContainTextAsync("Site web");
        await Expect(Page.GetByTestId("requests-table")).ToContainTextAsync("WhatsApp");
        await AssertAccessibleAsync();

        // The data table of a chart: hidden, then shown by its button, one row per elapsed day.
        var show = Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = $"Afficher le tableau : {RevPavChart}", Exact = true });
        var table = Page.Locator("#chart-revpav-table");
        await Expect(show).ToHaveAttributeAsync("aria-expanded", "false");
        await Expect(table).ToBeHiddenAsync();
        await show.ClickAsync();
        var hide = Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = $"Masquer le tableau : {RevPavChart}", Exact = true });
        await Expect(hide).ToHaveAttributeAsync("aria-expanded", "true");
        await Expect(table).ToBeVisibleAsync();
        await Expect(table.Locator("tbody tr")).ToHaveCountAsync(expected.Daily.Count);
        await Expect(table.Locator("tfoot")).ToContainTextAsync(Number(expected.Fleet.RevenuePerAvailableDay));
        await AssertAccessibleAsync();

        // The profitability table sorts on its headers (aria-sort tells which way); the most negative margin over the
        // window of the hint first: the vehicle to sell or replace, when there is one.
        var hintHeader = Page.GetByRole(AriaRole.Columnheader, new PageGetByRoleOptions { Name = $"Marge sur {expected.SellHintMonths} mois (TND)", Exact = true });
        await hintHeader.GetByRole(AriaRole.Button).ClickAsync();
        await Expect(hintHeader).ToHaveAttributeAsync("aria-sort", "ascending");
        if (expected.Vehicles.Any(v => v.SuggestSaleOrReplacement))
        {
            await Expect(vehicleRows.First.GetByTestId("sell-hint")).ToHaveTextAsync("À vendre ou à remplacer ?");
        }

        var vehicleHeader = Page.GetByRole(AriaRole.Columnheader, new PageGetByRoleOptions { Name = "Véhicule", Exact = true });
        await vehicleHeader.GetByRole(AriaRole.Button).ClickAsync();
        await Expect(vehicleHeader).ToHaveAttributeAsync("aria-sort", "ascending");
        var plates = expected.Vehicles.Select(v => v.PlateNumber).Order(StringComparer.Ordinal).ToList();
        await Expect(vehicleRows.First).ToContainTextAsync(plates[0]);
        await vehicleHeader.GetByRole(AriaRole.Button).ClickAsync();
        await Expect(vehicleHeader).ToHaveAttributeAsync("aria-sort", "descending");
        await Expect(vehicleRows.First).ToContainTextAsync(plates[^1]);

        // The Excel export of the table, built on the server and downloaded as an attachment.
        var export = Page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = "Exporter (Excel) : Rentabilité par véhicule", Exact = true });
        var download = await Page.RunAndWaitForDownloadAsync(() => export.ClickAsync());
        download.SuggestedFilename.ShouldStartWith("rapport-vehicules-agence-demo-tunis-");
        download.SuggestedFilename.ShouldEndWith(".xlsx");
        var directory = Directory.CreateTempSubdirectory("carrental-e2e-report-");
        try
        {
            var path = Path.Combine(directory.FullName, download.SuggestedFilename);
            await download.SaveAsAsync(path);
            using var workbook = new XLWorkbook(path);
            var sheet = workbook.Worksheet("Véhicules");
            sheet.Cell(1, 1).GetString().ShouldBe("Immatriculation");
            Enumerable.Range(2, expected.Vehicles.Count).Select(r => sheet.Cell(r, 1).GetString())
                .ShouldBe(expected.Vehicles.Select(v => v.PlateNumber), ignoreOrder: true);
            sheet.Cell(expected.Vehicles.Count + 2, 1).GetString().ShouldBe("Total");
            Math.Round((decimal)sheet.Cell(expected.Vehicles.Count + 2, 8).GetDouble(), 3).ShouldBe(expected.Fleet.Revenue.Amount);
        }
        finally
        {
            directory.Delete(recursive: true);
        }

        // The page stays as it was (a download is not a navigation).
        await Expect(Page.Locator("h1[data-ready=true]")).ToBeVisibleAsync();
        await AssertAccessibleAsync();
    }

    [Fact]
    public async Task An_agent_has_no_access_to_the_reports()
    {
        var agent = await BackOfficeSession.NewStaffAsync(Host, Roles.Agent);
        await BackOfficeSession.SignInAsync(Page, agent, "/admin");
        await BackOfficeSession.WaitUntilInteractiveAsync(Page);

        await Expect(Page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = "Rapports", Exact = true })).ToHaveCountAsync(0);
        await Expect(Page.GetByTestId("reports-widget")).ToHaveCountAsync(0);
        await Page.GotoAsync("/admin/reports");
        await Expect(Page).ToHaveURLAsync(new Regex("/account/access-denied"));
        await Expect(Page.GetByTestId("kpi-revpav")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task The_dashboard_shows_the_reports_tile_to_the_manager()
    {
        var manager = await BackOfficeSession.NewStaffAsync(Host, Roles.Manager);
        await BackOfficeSession.SignInAsync(Page, manager, "/admin");
        await BackOfficeSession.WaitUntilInteractiveAsync(Page);

        var tile = Page.GetByTestId("reports-widget");
        await Expect(tile).ToContainTextAsync("Revenu par véhicule disponible et par jour", new LocatorAssertionsToContainTextOptions { Timeout = 30_000 });
        await Expect(tile).ToContainTextAsync("TND");
        await Expect(tile.GetByRole(AriaRole.Link, new LocatorGetByRoleOptions { Name = "Ouvrir les rapports" })).ToHaveAttributeAsync("href", "/admin/reports");

        // The tile only: the other widgets of the dashboard belong to their own modules and tests.
        await BackOfficeSession.WaitForTransitionsAsync(Page);
        var result = await tile.RunAxe();
        result.Violations.Where(v => v.Impact is "critical" or "serious").Select(v => v.Id).ShouldBeEmpty();
    }

    /// <summary>The amount as the tiles write it (French culture of the browser).</summary>
    private static string Tnd(Money? amount) =>
        amount is { } value ? value.ToString("N", System.Globalization.CultureInfo.GetCultureInfo("fr")) : "—";

    /// <summary>The amount as the table cells write it: without the currency, named by the column header.</summary>
    private static string Number(Money? amount) =>
        amount is { } value ? value.Amount.ToString("N3", System.Globalization.CultureInfo.GetCultureInfo("fr")) : "—";

    private async Task<Guid> DemoAgencyIdAsync()
    {
        await using var scope = Host.Factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ScopedUserContext>().Initialize(UserContextSnapshot.System("system:e2e"));
        var agencies = await scope.ServiceProvider.GetRequiredService<IReferentialQueries>().GetAgenciesAsync(Ct);
        return agencies.Single(a => a.Name == DemoAgencyName).Id;
    }

    /// <summary>What the screen must show for the default period (the current month, the previous one on its first day).</summary>
    private async Task<ReportDashboard> ExpectedReportAsync(Guid agencyId)
    {
        var period = ReportPeriod.DefaultFor(TunisTime.Today(TimeProvider.System));
        await using var scope = Host.Factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ScopedUserContext>()
            .Initialize(TestActors.Staff(agencyId, Roles.Accountant, "comptable-e2e@tests.local"));
        var report = await scope.ServiceProvider.GetRequiredService<ReportQueries>().GetDashboardAsync(new ReportRequest(agencyId, period.From, period.To), Ct);
        report.IsSuccess.ShouldBeTrue(report.IsFailure ? report.Error.ToString() : null);
        report.Value.Vehicles.ShouldNotBeEmpty("the demo seeding computes the snapshots of the last days");
        return report.Value;
    }

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
}
