using System.Net;
using CarRental.Application.Identity.Contracts;
using CarRental.Application.Reporting.Indicators;
using CarRental.Application.Reporting.Snapshots;
using CarRental.Domain.Common;
using CarRental.Integration.Tests.Support;
using CarRental.Web.Components.Admin.Reports;
using ClosedXML.Excel;
using Microsoft.Extensions.DependencyInjection;
using static CarRental.Integration.Tests.Reporting.ReportingWorld;

namespace CarRental.Integration.Tests.Reporting;

/// <summary>
/// The Excel exports of the reports over HTTP (ADR-0046): built on the server from the same indicators as the screen,
/// downloaded with an MFA session and reports.read only, never cached, in the user's language, and read back with ClosedXML.
/// </summary>
public sealed class ReportExportTests(IntegrationHost host)
{
    private const string Xlsx = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Two vehicles over the last ten days: one rented 5 days (500.000 HT) by phone, one 2 days (200.000 HT) on Booking.com.</summary>
    private async Task<ReportingAgency> TwoRentedVehiclesAsync()
    {
        var agency = await CreateAsync(host, vehicles: 2, Ct);
        await PastFileAsync(host, agency, agency.Vehicles[0], At(-8), days: 5, rentalInclVat: 595m, PastOutcome.Closed, Ct);
        await PastFileAsync(host, agency, agency.Vehicles[1], At(-6), days: 2, rentalInclVat: 238m, PastOutcome.Returned, Ct, SalesChannel.BookingCom);
        await using var scope = host.CreateScope(Manager);
        var run = await scope.ServiceProvider.GetRequiredService<SnapshotService>().RecomputeAsync(agency.AgencyId, Day(-10), Day(-1), Ct);
        run.IsSuccess.ShouldBeTrue(run.IsFailure ? run.Error.ToString() : null);
        return agency;
    }

    private async Task<ReportDashboard> DashboardAsync(Guid agencyId)
    {
        await using var scope = host.CreateScope(Accountant);
        var report = await scope.ServiceProvider.GetRequiredService<ReportQueries>().GetDashboardAsync(new ReportRequest(agencyId, Day(-10), Day(-1)), Ct);
        report.IsSuccess.ShouldBeTrue(report.IsFailure ? report.Error.ToString() : null);
        return report.Value;
    }

    private async Task<HttpClient> SignedInAsync(string role, string? language = null)
    {
        var client = host.CreateClient();
        if (language is not null)
        {
            client.DefaultRequestHeaders.AcceptLanguage.ParseAdd(language);
        }

        var (_, email) = await TestStaff.CreateAsync(host, role);
        await HttpSignIn.SignInWithTwoFactorAsync(client, email, "/admin", Ct);
        return client;
    }

    private static Uri Export(ReportTable table, Guid agencyId) => new(ReportExport.Url(table, agencyId, Day(-10), Day(-1)), UriKind.Relative);

    private static XLWorkbook Workbook(byte[] content) => new(new MemoryStream(content));

    private static decimal Number(IXLCell cell) => Math.Round((decimal)cell.GetDouble(), 3);

    [Fact]
    public async Task A_manager_downloads_the_profitability_of_each_vehicle_as_an_excel_workbook()
    {
        var agency = await TwoRentedVehiclesAsync();
        var report = await DashboardAsync(agency.AgencyId);
        using var client = await SignedInAsync(Roles.Manager);

        using var response = await client.GetAsync(Export(ReportTable.Vehicles, agency.AgencyId), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe(Xlsx);
        response.Headers.CacheControl!.NoStore.ShouldBeTrue();
        var disposition = response.Content.Headers.ContentDisposition!;
        disposition.DispositionType.ShouldBe("attachment");
        disposition.FileName!.Trim('"').ShouldBe($"rapport-vehicules-{ReportExport.Slug(report.AgencyName)}-{Day(-10):yyyy-MM-dd}-{Day(-1):yyyy-MM-dd}.xlsx");

        using var workbook = Workbook(await response.Content.ReadAsByteArrayAsync(Ct));
        workbook.Worksheets.Select(w => w.Name).ShouldBe(["Véhicules", "Synthèse"]);
        var sheet = workbook.Worksheet("Véhicules");
        sheet.Cell(1, 1).GetString().ShouldBe("Immatriculation");
        sheet.Cell(1, 8).GetString().ShouldBe("CA location HT (TND)");
        var rows = Enumerable.Range(2, 2).Select(r => sheet.Row(r)).ToList();
        rows.Select(r => r.Cell(1).GetString()).ShouldBe(report.Vehicles.Select(v => v.PlateNumber));
        var rented = rows.Single(r => r.Cell(6).GetDouble() == 5);
        rented.Cell(5).GetDouble().ShouldBe(10);
        Number(rented.Cell(7)).ShouldBe(0.5m);
        Number(rented.Cell(8)).ShouldBe(500m);
        Number(rows.Single(r => r.Cell(6).GetDouble() == 2).Cell(8)).ShouldBe(200m);
        rented.Cell(8).Style.NumberFormat.Format.ShouldBe("#,##0.000");

        // The total row, below the filtered rows, holds the fleet totals of the screen.
        var total = sheet.Row(4);
        total.Cell(1).GetString().ShouldBe("Total");
        total.Cell(5).GetDouble().ShouldBe(20);
        total.Cell(6).GetDouble().ShouldBe(7);
        Number(total.Cell(8)).ShouldBe(700m);
        Number(total.Cell(12)).ShouldBe(report.Fleet.Margin.Amount);
        Number(total.Cell(13)).ShouldBe(35m);
        sheet.AutoFilter.Range.RangeAddress.LastAddress.RowNumber.ShouldBe(3);

        var summary = workbook.Worksheet("Synthèse");
        summary.Cell(2, 2).GetString().ShouldBe(report.AgencyName);
    }

    [Fact]
    public async Task The_full_export_has_one_sheet_per_table_with_the_figures_of_the_screen()
    {
        var agency = await TwoRentedVehiclesAsync();
        var report = await DashboardAsync(agency.AgencyId);
        using var client = await SignedInAsync(Roles.Accountant);

        using var response = await client.GetAsync(Export(ReportTable.All, agency.AgencyId), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var workbook = Workbook(await response.Content.ReadAsByteArrayAsync(Ct));
        workbook.Worksheets.Select(w => w.Name).ShouldBe(
            ["Synthèse", "Jours", "Véhicules", "Canaux", "Rythme", "Conversion", "Demandes", "Annulations", "Trésorerie", "Impayés"]);

        // One row per day of the period, then the total.
        var days = workbook.Worksheet("Jours");
        Enumerable.Range(2, 10).Select(r => DateOnly.FromDateTime(days.Cell(r, 1).GetDateTime())).ShouldBe(Enumerable.Range(-10, 10).Select(Day));
        days.Cell(12, 1).GetString().ShouldBe("Total");
        Number(days.Cell(12, 6)).ShouldBe(700m);

        // One row per channel, the days without rental, then the total.
        var channels = workbook.Worksheet("Canaux");
        Enumerable.Range(2, 7).Select(r => channels.Cell(r, 1).GetString()).ShouldBe(
            ["Site web", "WhatsApp", "Téléphone", "Comptoir", "Booking.com", "Jours sans location", "Total"]);
        Number(channels.Cell(4, 4)).ShouldBe(500m);
        Number(channels.Cell(6, 4)).ShouldBe(200m);
        channels.Cell(6, 2).GetDouble().ShouldBe(1);
        channels.Cell(6, 3).GetDouble().ShouldBe(2);
        Number(channels.Cell(8, 4)).ShouldBe(700m);
        Number(channels.Cell(8, 9)).ShouldBe(report.Channels.Margin.Amount);
        Number(channels.Cell(8, 9)).ShouldBe(report.Fleet.Margin.Amount);

        // The summary leads with the manager's main indicator.
        var summary = workbook.Worksheet("Synthèse");
        var revPav = summary.RowsUsed().Single(r => r.Cell(1).GetString() == "Revenu par véhicule disponible et par jour (TND)");
        Number(revPav.Cell(2)).ShouldBe(35m);
        // Every source is connected (channel commissions, return charges, invoicing): no note says otherwise.
        summary.RowsUsed().ShouldNotContain(r => r.Cell(1).GetString().Contains("commissions des canaux", StringComparison.Ordinal));
        summary.RowsUsed().ShouldNotContain(r => r.Cell(1).GetString().Contains("frais de retour ne sont pas", StringComparison.Ordinal));
        summary.RowsUsed().ShouldNotContain(r => r.Cell(1).GetString().Contains("facturation n'est pas encore", StringComparison.Ordinal));

        // The unpaid files: the returned one owes its whole quote, the closed one too (nothing paid in this world).
        var unpaid = workbook.Worksheet("Impayés");
        unpaid.Cell(1, 1).GetString().ShouldBe("Dossier");
        unpaid.RowsUsed().Count().ShouldBe(report.Cash.Unpaid.Count + 2);
    }

    [Fact]
    public async Task An_english_user_gets_english_sheets_and_file_name()
    {
        var agency = await TwoRentedVehiclesAsync();
        using var client = await SignedInAsync(Roles.Manager, "en");

        using var response = await client.GetAsync(Export(ReportTable.Channels, agency.AgencyId), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentDisposition!.FileName!.Trim('"').ShouldStartWith("report-channels-");
        using var workbook = Workbook(await response.Content.ReadAsByteArrayAsync(Ct));
        workbook.Worksheets.Select(w => w.Name).ShouldBe(["Channels", "Summary"]);
        var channels = workbook.Worksheet("Channels");
        channels.Cell(1, 1).GetString().ShouldBe("Channel");
        Enumerable.Range(2, 5).Select(r => channels.Cell(r, 1).GetString()).ShouldBe(["Website", "WhatsApp", "Phone", "Counter", "Booking.com"]);
        workbook.Worksheet("Summary").Cell(2, 1).GetString().ShouldBe("Agency");
    }

    [Fact]
    public async Task Staff_without_reports_read_are_refused_and_visitors_sent_to_sign_in()
    {
        var agency = await TwoRentedVehiclesAsync();
        using var agent = await SignedInAsync(Roles.Agent);
        using var anonymous = host.CreateClient();

        using var refused = await agent.GetAsync(Export(ReportTable.Vehicles, agency.AgencyId), Ct);
        using var visitor = await anonymous.GetAsync(Export(ReportTable.Vehicles, agency.AgencyId), Ct);

        refused.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        HttpSignIn.PathAndQuery(refused.Headers.Location!).ShouldContain("/account/access-denied");
        visitor.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        HttpSignIn.PathAndQuery(visitor.Headers.Location!).ShouldContain("/account/login");
    }

    [Fact]
    public async Task Unknown_tables_agencies_and_malformed_periods_are_rejected()
    {
        var agency = await CreateAsync(host, vehicles: 1, Ct);
        using var client = await SignedInAsync(Roles.Manager);
        var period = $"from={Day(-10):yyyy-MM-dd}&to={Day(-1):yyyy-MM-dd}";

        async Task<HttpStatusCode> StatusOf(string url)
        {
            using var response = await client.GetAsync(new Uri(url, UriKind.Relative), Ct);
            return response.StatusCode;
        }

        (await StatusOf($"/admin/reports/export/payroll?agency={agency.AgencyId}&{period}")).ShouldBe(HttpStatusCode.NotFound);
        (await StatusOf($"/admin/reports/export/2?agency={agency.AgencyId}&{period}")).ShouldBe(HttpStatusCode.NotFound);
        (await StatusOf($"/admin/reports/export/vehicles?{period}")).ShouldBe(HttpStatusCode.BadRequest);
        (await StatusOf($"/admin/reports/export/vehicles?agency={agency.AgencyId}&from=21/10/2026&to={Day(-1):yyyy-MM-dd}")).ShouldBe(HttpStatusCode.BadRequest);
        (await StatusOf($"/admin/reports/export/vehicles?agency={agency.AgencyId}&from={Day(-1):yyyy-MM-dd}&to={Day(-10):yyyy-MM-dd}")).ShouldBe(HttpStatusCode.BadRequest);
        (await StatusOf($"/admin/reports/export/vehicles?agency={agency.AgencyId}&from={Day(-400):yyyy-MM-dd}&to={Day(-1):yyyy-MM-dd}")).ShouldBe(HttpStatusCode.BadRequest);
        (await StatusOf($"/admin/reports/export/vehicles?agency={Guid.CreateVersion7()}&{period}")).ShouldBe(HttpStatusCode.NotFound);
        (await StatusOf($"/admin/reports/export/vehicles?agency={agency.AgencyId}&{period}")).ShouldBe(HttpStatusCode.OK);
    }
}
