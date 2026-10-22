using System.Net;
using System.Text;
using CarRental.Application.Cash.Contracts;
using CarRental.Application.Cash.Expenses;
using CarRental.Application.Identity.Contracts;
using CarRental.Application.Payments.Contracts;
using CarRental.Domain.Accounting;
using CarRental.Domain.Common;
using CarRental.Integration.Tests.Payments;
using CarRental.Integration.Tests.Support;
using ClosedXML.Excel;
using Microsoft.Extensions.DependencyInjection;

namespace CarRental.Integration.Tests.Accounting;

/// <summary>
/// The endpoints of the pre-accounting over HTTP (ADR-0065): the export of the journals of the agency of the session in
/// the generic CSV and Excel formats, never cached, for <c>accounting.manage</c> only; the receipts of the expenses,
/// private, revalidated by entity tag, for <c>expenses.manage</c> only.
/// </summary>
public sealed class AccountingWebTests(IntegrationHost host)
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_journals_of_the_month_export_as_csv_and_excel_for_the_accountant_only()
    {
        var agencyId = await FinanceData.NewAgencyAsync(host, Ct);
        await using (var scope = host.CreateScope(FinanceData.StaffOf(agencyId, Roles.Agent)))
        {
            (await scope.ServiceProvider.GetRequiredService<ICounterPayments>().RecordPaymentAsync(
                new CounterPaymentRequest(Guid.CreateVersion7(), agencyId, PaymentMethodInfo.CardTerminal, 245.5m, "TPE-WEB-1"),
                Ct)).IsSuccess.ShouldBeTrue();
        }

        var month = AccountingMonth.Of(TunisTime.Today(TimeProvider.System));
        var url = $"/admin/accounting/export?from={month}&to={month}&journal=all&format=csv";
        using var anonymous = host.CreateClient();
        var refused = await anonymous.GetAsync(new Uri(url, UriKind.Relative), Ct);
        refused.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        HttpSignIn.PathAndQuery(refused.Headers.Location!).ShouldContain("/account/login");

        using var agent = host.CreateClient();
        await HttpSignIn.SignInWithTwoFactorAsync(agent, (await TestStaff.CreateAsync(host, Roles.Agent, agencyId: agencyId)).Email, "/admin", Ct);
        var denied = await agent.GetAsync(new Uri(url, UriKind.Relative), Ct);
        denied.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        HttpSignIn.PathAndQuery(denied.Headers.Location!).ShouldContain("/account/access-denied");

        using var accountant = host.CreateClient();
        await HttpSignIn.SignInWithTwoFactorAsync(accountant, (await TestStaff.CreateAsync(host, Roles.Accountant, agencyId: agencyId)).Email, "/admin", Ct);
        var csv = await accountant.GetAsync(new Uri(url, UriKind.Relative), Ct);

        csv.StatusCode.ShouldBe(HttpStatusCode.OK);
        csv.Content.Headers.ContentType!.MediaType.ShouldBe("text/csv");
        csv.Content.Headers.ContentDisposition!.FileNameStar.ShouldBe($"journaux-tous-{month}.csv");
        csv.Headers.CacheControl!.NoStore.ShouldBeTrue();
        var text = Encoding.UTF8.GetString(await csv.Content.ReadAsByteArrayAsync(Ct)).TrimStart('﻿');
        var rows = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        rows[0].ShouldBe("date;journal;compte;piece;libelle;debit;credit;lettrage");
        rows[1..^1].Select(r => r.Split(';')[2]).ShouldBe(["532", "411"]);
        rows[1..^1].ShouldAllBe(r => r.Split(';')[1] == "BQ");
        rows[^1].ShouldBe("TOTAL;;;;;245.500;245.500;");

        var xlsx = await accountant.GetAsync(new Uri(url.Replace("format=csv", "format=xlsx", StringComparison.Ordinal).Replace("journal=all", "journal=BQ", StringComparison.Ordinal), UriKind.Relative), Ct);
        xlsx.StatusCode.ShouldBe(HttpStatusCode.OK);
        xlsx.Content.Headers.ContentType!.MediaType.ShouldBe("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        using var workbook = new XLWorkbook(new MemoryStream(await xlsx.Content.ReadAsByteArrayAsync(Ct)));
        var lines = workbook.Worksheet("Écritures");
        (lines.Cell(2, 2).GetString(), lines.Cell(2, 3).GetString(), lines.Cell(2, 6).GetValue<decimal>()).ShouldBe(("BQ", "532", 245.5m));
        (lines.Cell(3, 3).GetString(), lines.Cell(3, 7).GetValue<decimal>()).ShouldBe(("411", 245.5m));
        workbook.Worksheet("Contrôle").Cell(9, 2).GetString().ShouldBe("Oui", "balanced");

        (await accountant.GetAsync(new Uri("/admin/accounting/export?from=2026-13&format=csv", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await accountant.GetAsync(new Uri(url.Replace("format=csv", "format=sage", StringComparison.Ordinal), UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await accountant.GetAsync(new Uri(url.Replace("journal=all", "journal=XX", StringComparison.Ordinal), UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task The_export_and_the_receipts_follow_the_modules_of_the_agency()
    {
        var agencyId = await FinanceData.NewAgencyAsync(host, Ct);
        Guid expenseId;
        await using (var scope = host.CreateScope(FinanceData.StaffOf(agencyId, Roles.Accountant)))
        {
            var expenses = scope.ServiceProvider.GetRequiredService<ExpenseService>();
            expenseId = (await expenses.RecordAsync(Form(agencyId, TunisTime.Today(TimeProvider.System)), Ct)).Value;
            (await expenses.UploadReceiptAsync(expenseId, "ticket.png", new MemoryStream(Png), Ct)).IsSuccess.ShouldBeTrue();
        }

        using var accountant = host.CreateClient();
        await HttpSignIn.SignInWithTwoFactorAsync(accountant, (await TestStaff.CreateAsync(host, Roles.Accountant, agencyId: agencyId)).Email, "/admin", Ct);
        var month = AccountingMonth.Of(TunisTime.Today(TimeProvider.System));
        var export = new Uri($"/admin/accounting/export?from={month}&format=csv", UriKind.Relative);
        var receipt = new Uri($"/admin/expenses/{expenseId}/receipt", UriKind.Relative);
        (await accountant.GetAsync(export, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await accountant.GetAsync(receipt, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        await FinanceData.SetModulesAsync(host, agencyId, AgencyModuleCatalog.Defaults & ~(AgencyModules.Accounting | AgencyModules.Cash), Ct);

        HttpSignIn.PathAndQuery((await accountant.GetAsync(export, Ct)).Headers.Location!).ShouldContain("/account/access-denied", customMessage: "the accounting module is off");
        HttpSignIn.PathAndQuery((await accountant.GetAsync(receipt, Ct)).Headers.Location!).ShouldContain("/account/access-denied", customMessage: "the cash module is off");
    }

    [Fact]
    public async Task A_receipt_is_served_privately_to_the_expense_managers_and_revalidated_by_its_entity_tag()
    {
        var agencyId = await FinanceData.NewAgencyAsync(host, Ct);
        var bookkeeper = FinanceData.StaffOf(agencyId, Roles.Accountant);
        Guid withReceipt;
        Guid withoutReceipt;
        await using (var scope = host.CreateScope(bookkeeper))
        {
            var expenses = scope.ServiceProvider.GetRequiredService<ExpenseService>();
            var today = TunisTime.Today(TimeProvider.System);
            withReceipt = (await expenses.RecordAsync(Form(agencyId, today), Ct)).Value;
            withoutReceipt = (await expenses.RecordAsync(Form(agencyId, today), Ct)).Value;
            (await expenses.UploadReceiptAsync(withReceipt, "ticket station.png", new MemoryStream(Png), Ct)).IsSuccess.ShouldBeTrue();
        }

        var url = new Uri($"/admin/expenses/{withReceipt}/receipt", UriKind.Relative);
        using var anonymous = host.CreateClient();
        var refused = await anonymous.GetAsync(url, Ct);
        refused.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        HttpSignIn.PathAndQuery(refused.Headers.Location!).ShouldContain("/account/login");

        using var agent = host.CreateClient();
        await HttpSignIn.SignInWithTwoFactorAsync(agent, (await TestStaff.CreateAsync(host, Roles.Agent, agencyId: agencyId)).Email, "/admin", Ct);
        HttpSignIn.PathAndQuery((await agent.GetAsync(url, Ct)).Headers.Location!).ShouldContain("/account/access-denied");

        using var accountant = host.CreateClient();
        await HttpSignIn.SignInWithTwoFactorAsync(accountant, (await TestStaff.CreateAsync(host, Roles.Accountant, agencyId: agencyId)).Email, "/admin", Ct);
        var served = await accountant.GetAsync(url, Ct);

        served.StatusCode.ShouldBe(HttpStatusCode.OK);
        served.Content.Headers.ContentType!.MediaType.ShouldBe("image/png");
        (await served.Content.ReadAsByteArrayAsync(Ct)).ShouldBe(Png);
        served.Headers.CacheControl!.Private.ShouldBeTrue();
        served.Headers.CacheControl.NoCache.ShouldBeTrue();
        served.Content.Headers.ContentDisposition!.DispositionType.ShouldBe("inline");
        var entityTag = served.Headers.ETag.ShouldNotBeNull();
        using var revalidation = new HttpRequestMessage(HttpMethod.Get, url);
        revalidation.Headers.IfNoneMatch.Add(entityTag);
        (await accountant.SendAsync(revalidation, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotModified);
        (await accountant.GetAsync(new Uri($"/admin/expenses/{withoutReceipt}/receipt", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await accountant.GetAsync(new Uri($"/admin/expenses/{Guid.CreateVersion7()}/receipt", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    private static ExpenseForm Form(Guid agencyId, DateOnly day) =>
        new(agencyId, day, ExpenseCategoryInfo.Fuel, "Station Agil", ExpenseAmountMode.InclVatAndRate, 59.5m, null, null, 0.19m, ExpensePaymentMethodInfo.Card, "TPE-RECU");
}
