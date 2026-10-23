using System.Net;
using System.Text;
using CarRental.Application.Accounting.Chart;
using CarRental.Application.Accounting.Closing;
using CarRental.Application.Accounting.Journals;
using CarRental.Application.Accounting.Reconciliation;
using CarRental.Application.Billing.Administration;
using CarRental.Application.Billing.Claims;
using CarRental.Application.Billing.Fines;
using CarRental.Application.Billing.Invoicing;
using CarRental.Application.Cash.Contracts;
using CarRental.Application.Cash.Expenses;
using CarRental.Application.Common.Paging;
using CarRental.Application.Common.Persistence;
using CarRental.Application.Identity.Contracts;
using CarRental.Application.Payments.Administration;
using CarRental.Application.Payments.Contracts;
using CarRental.Domain.Accounting;
using CarRental.Domain.Billing;
using CarRental.Domain.Cash;
using CarRental.Domain.Cash.Expenses;
using CarRental.Domain.Common;
using CarRental.Domain.Payments;
using CarRental.Integration.Tests.Support;
using CarRental.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static CarRental.Integration.Tests.Billing.BillingWorld;
using static CarRental.Integration.Tests.Bookings.BookingWorld;
using static CarRental.Integration.Tests.Bookings.Handover.HandoverWorld;

namespace CarRental.Integration.Tests.Payments;

/// <summary>
/// The agencies never see each other's money (ADR-0067), on SQL Server: the staff of an agency lists and reads its own
/// payments, deposits, invoices, credit notes, fines, claims, cash sessions, expenses, journals and chart of accounts only,
/// never records anything for another agency; the numbers and the account numbers of each agency are its own, and an
/// agency without the cash module takes no cash.
/// </summary>
public sealed class FinanceTenancyTests(IntegrationHost host)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_payments_and_deposits_of_an_agency_are_never_shown_to_another_nor_recorded_for_it()
    {
        var (a, b) = (await FinanceData.NewAgencyAsync(host, Ct), await FinanceData.NewAgencyAsync(host, Ct));
        var (bookingA, bookingB) = (await TestBookings.NewAsync(host, a, Ct), await TestBookings.NewAsync(host, b, Ct));
        var paymentA = Ok(await RunAsync<ICounterPayments, Result<Guid>>(
            host,
            FinanceData.StaffOf(a, Roles.Agent),
            p => p.RecordPaymentAsync(new CounterPaymentRequest(bookingA, a, PaymentMethodInfo.CardTerminal, 100m, "TPE-A"), Ct)));
        var paymentB = Ok(await RunAsync<ICounterPayments, Result<Guid>>(
            host,
            FinanceData.StaffOf(b, Roles.Agent),
            p => p.RecordPaymentAsync(new CounterPaymentRequest(bookingB, b, PaymentMethodInfo.BankTransfer, 200m, "VIR-B", AwaitingConfirmation: true), Ct)));
        Ok(await RunAsync<IDeposits, Result<Guid>>(
            host,
            FinanceData.StaffOf(b, Roles.Agent),
            d => d.TakeAsync(new DepositRequest(bookingB, b, DepositModeInfo.CardImprint, 500m, "AUT-B"), Ct)));

        var managerA = FinanceData.StaffOf(a, Roles.Manager);
        var payments = Ok(await RunAsync<PaymentAdminQueries, Result<PaymentList>>(host, managerA, q => q.ListPaymentsAsync(new PaymentListFilter(), new PageRequest(1, 100), Ct)));
        payments.Page.Items.Select(p => p.Id).ShouldBe([paymentA]);
        Ok(await RunAsync<PaymentAdminQueries, Result<DepositList>>(host, managerA, q => q.ListDepositsAsync(new DepositListFilter(), new PageRequest(1, 100), Ct))).Page.Items.ShouldBeEmpty();
        Ok(await RunAsync<ICounterPayments, Result<BookingPayments>>(host, managerA, p => p.GetBookingPaymentsAsync(bookingB, Ct))).Payments.ShouldBeEmpty();
        Ok(await RunAsync<IDeposits, Result<BookingDeposits>>(host, managerA, d => d.GetBookingDepositsAsync(bookingB, Ct))).Deposits.ShouldBeEmpty();
        (await RunAsync<IPaymentsQueries, BookingPaymentSummary>(host, managerA, q => q.GetBookingSummaryAsync(bookingB, Ct))).Payments.Pending.ShouldBe(Money.Zero);

        // Writes for the other agency are refused like those of an unknown agency or payment: nothing is written.
        (await RunAsync<ICounterPayments, Result<Guid>>(host, managerA, p => p.RecordPaymentAsync(new CounterPaymentRequest(bookingB, b, PaymentMethodInfo.CardTerminal, 50m, "TPE-X"), Ct)))
            .Error.ShouldBe(PaymentsErrors.AgencyNotFound);
        (await RunAsync<IDeposits, Result<Guid>>(host, managerA, d => d.TakeAsync(new DepositRequest(bookingB, b, DepositModeInfo.CardImprint, 50m, "AUT-X"), Ct)))
            .Error.ShouldBe(PaymentsErrors.AgencyNotFound);
        (await RunAsync<ICounterPayments, Result>(host, managerA, p => p.ConfirmPendingAsync(paymentB, "VIR-B", Ct))).Error.ShouldBe(PaymentsErrors.PaymentNotFound);
        (await RunAsync<ICounterPayments, Result>(host, managerA, p => p.CancelPendingAsync(paymentB, "Jamais reçu", Ct))).Error.ShouldBe(PaymentsErrors.PaymentNotFound);

        // Nor is money of the agency recorded for the other agency's booking (by its id), even by the support who sees
        // it: refused like an unknown booking, which never tells that the booking exists elsewhere.
        var link = new OnlinePaymentRequest(bookingB, a, 50m, "fr", "/fr/ok", "/fr/ko", IsLink: true);
        foreach (var actor in new[] { managerA, TestActors.Support() })
        {
            (await RunAsync<ICounterPayments, Result<Guid>>(host, actor, p => p.RecordPaymentAsync(new CounterPaymentRequest(bookingB, a, PaymentMethodInfo.CardTerminal, 50m, "TPE-X"), Ct)))
                .Error.ShouldBe(PaymentsErrors.BookingNotFound);
            (await RunAsync<IDeposits, Result<Guid>>(host, actor, d => d.TakeAsync(new DepositRequest(bookingB, a, DepositModeInfo.CardImprint, 50m, "AUT-X"), Ct)))
                .Error.ShouldBe(PaymentsErrors.BookingNotFound);
            (await RunAsync<OnlinePaymentAdministration, Result<OnlinePaymentRedirect>>(host, actor, o => o.CreatePaymentLinkAsync(link, Ct)))
                .Error.ShouldBe(PaymentsErrors.BookingNotFound);
        }

        (await RunAsync<ICounterPayments, Result<Guid>>(host, managerA, p => p.RecordPaymentAsync(new CounterPaymentRequest(Guid.CreateVersion7(), a, PaymentMethodInfo.CardTerminal, 50m, "TPE-Y"), Ct)))
            .Error.ShouldBe(PaymentsErrors.BookingNotFound);
        (await RunAsync<IOnlinePayments, Result<OnlinePaymentRedirect>>(host, TestActors.Visitor(a), o => o.StartPaymentAsync(link with { IsLink = false }, Ct)))
            .Error.ShouldBe(PaymentsErrors.BookingNotFound);

        await using var check = host.CreateScope();
        var db = check.ServiceProvider.GetRequiredService<IAppDbContext>();
        (await db.Set<Payment>().CountAsync(p => p.AgencyId == b, Ct)).ShouldBe(1, "nothing was recorded for the other agency");
        (await db.Set<Payment>().SingleAsync(p => p.Id == paymentB, Ct)).Status.ShouldBe(PaymentStatus.Pending);
        (await db.Set<DepositHold>().CountAsync(d => d.AgencyId == b, Ct)).ShouldBe(1);
        (await db.Set<Payment>().CountAsync(p => p.AgencyId == a, Ct)).ShouldBe(1, "nothing was recorded for the other agency's booking");
        (await db.Set<DepositHold>().CountAsync(d => d.AgencyId == a, Ct)).ShouldBe(0);
    }

    [Fact]
    public async Task The_invoices_credit_notes_fines_and_claims_of_an_agency_are_its_own_and_numbered_in_its_own_series()
    {
        var settings = new Dictionary<string, string> { ["billing.issue_automatically"] = "false" };
        var (a, b) = (await AgencyAsync(host, Ct, settings: settings), await AgencyAsync(host, Ct, settings: settings));
        var (fileA, fileB) = ((await ClosedFilesAsync(host, a, 1, Ct)).Single(), (await ClosedFilesAsync(host, b, 1, Ct)).Single());
        var invoiceA = Ok(await RunAsync<InvoicingService, Result<IssuedDocument>>(host, Accountant, s => s.IssueRentalInvoiceAsync(fileA, Ct)));
        var invoiceB = Ok(await RunAsync<InvoicingService, Result<IssuedDocument>>(host, Accountant, s => s.IssueRentalInvoiceAsync(fileB, Ct)));
        var noteB = Ok(await RunAsync<InvoicingService, Result<IssuedDocument>>(host, Accountant, s => s.CreateCreditNoteAsync(invoiceB.Id, new CreditNoteRequest(CreditNoteScope.Total, "Annulation du solde"), Ct)));
        var fineB = Ok(await RunAsync<FineService, Result<Guid>>(
            host,
            BillingAgent,
            s => s.RecordAsync(new FineForm(b.AgencyId, $"PV{Guid.NewGuid():N}"[..18], "Garde nationale", DateTimeOffset.UtcNow.AddDays(-1), "Route de Bizerte", "999 TU 9999", 40m), Ct)));
        var claimB = Ok(await RunAsync<ClaimService, Result<Guid>>(
            host,
            BillingAgent,
            s => s.OpenAsync(new ClaimForm(b.AgencyId, b.Agency.EcmrVehicles[0], null, DateTimeOffset.UtcNow.AddHours(-3), "Sousse", "Rayure de la portière", null, null, 0m), Ct)));

        // Each agency has its own series (ADR-0067): the first invoice of each is number 1 of the year.
        invoiceA.Number.ShouldBe(invoiceB.Number);
        invoiceA.Number.ShouldEndWith("-000001");
        noteB.Number.ShouldEndWith("-000001");

        var accountantA = FinanceData.StaffOf(a.AgencyId, Roles.Accountant);
        var invoices = Ok(await RunAsync<BillingAdministrationQueries, Result<PagedResult<InvoiceListItem>>>(host, accountantA, q => q.ListInvoicesAsync(new InvoiceListFilter(), new PageRequest(1, 100), Ct)));
        invoices.Items.Select(i => i.Id).ShouldBe([invoiceA.Id]);
        Ok(await RunAsync<BillingAdministrationQueries, Result<PagedResult<CreditNoteListItem>>>(host, accountantA, q => q.ListCreditNotesAsync(null, null, null, new PageRequest(1, 100), Ct)))
            .Items.ShouldBeEmpty();
        (await RunAsync<BillingAdministrationQueries, Result<InvoiceDetail>>(host, accountantA, q => q.GetInvoiceAsync(invoiceB.Id, Ct))).Error.ShouldBe(BillingErrors.InvoiceNotFound);
        (await RunAsync<BillingAdministrationQueries, Result<BillingPdfFile>>(host, accountantA, q => q.FindPdfAsync(BillingDocumentType.CreditNote, noteB.Id, Ct)))
            .Error.ShouldBe(BillingErrors.CreditNoteNotFound);
        Ok(await RunAsync<BillingAdministrationQueries, Result<IReadOnlyList<FileToInvoice>>>(host, accountantA, q => q.GetFilesToInvoiceAsync(b.AgencyId, Ct))).ShouldBeEmpty();
        Ok(await RunAsync<FineService, Result<PagedResult<FineListItem>>>(host, accountantA, s => s.ListAsync(new FineListFilter(), new PageRequest(1, 100), Ct))).Items.ShouldBeEmpty();
        (await RunAsync<FineService, Result<FineDetail>>(host, accountantA, s => s.GetAsync(fineB, Ct))).Error.ShouldBe(BillingErrors.FineNotFound);
        Ok(await RunAsync<ClaimService, Result<PagedResult<ClaimListItem>>>(host, accountantA, s => s.ListAsync(new ClaimListFilter(), new PageRequest(1, 100), Ct))).Items.ShouldBeEmpty();
        (await RunAsync<ClaimService, Result<ClaimDetail>>(host, accountantA, s => s.GetAsync(claimB, Ct))).Error.ShouldBe(BillingErrors.ClaimNotFound);

        // Nothing is billed nor recorded for the other agency.
        (await RunAsync<InvoicingService, Result<IssuedDocument>>(host, accountantA, s => s.CreateCreditNoteAsync(invoiceB.Id, new CreditNoteRequest(CreditNoteScope.Total, "Intrusion"), Ct)))
            .Error.ShouldBe(BillingErrors.InvoiceNotFound);
        (await RunAsync<InvoicingService, Result<IssuedDocument>>(host, accountantA, s => s.IssueRentalInvoiceAsync(fileB, Ct))).IsFailure.ShouldBeTrue();
        (await RunAsync<ClaimService, Result<Guid>>(
                host,
                accountantA,
                s => s.OpenAsync(new ClaimForm(b.AgencyId, b.Agency.EcmrVehicles[0], null, DateTimeOffset.UtcNow, null, "Intrusion", null, null, 0m), Ct)))
            .Error.ShouldBe(BillingErrors.ClaimVehicleRequired);
        (await RunAsync<FineService, Result<Guid>>(
                host,
                accountantA,
                s => s.RecordAsync(new FineForm(b.AgencyId, $"PV{Guid.NewGuid():N}"[..18], "Police", DateTimeOffset.UtcNow, "Tunis", "999 TU 9999", 40m), Ct)))
            .IsFailure.ShouldBeTrue();

        await using var check = host.CreateScope();
        var db = check.ServiceProvider.GetRequiredService<IAppDbContext>();
        (await db.Set<Invoice>().CountAsync(i => i.AgencyId == b.AgencyId, Ct)).ShouldBe(1);
        (await db.Set<CreditNote>().CountAsync(c => c.AgencyId == b.AgencyId, Ct)).ShouldBe(1);
        (await db.Set<Fine>().CountAsync(f => f.AgencyId == b.AgencyId, Ct)).ShouldBe(1);
        (await db.Set<Claim>().CountAsync(c => c.AgencyId == b.AgencyId, Ct)).ShouldBe(1);
        (await db.Set<InvoiceLine>().Where(l => l.InvoiceId == invoiceB.Id).Select(l => l.AgencyId).Distinct().ToListAsync(Ct)).ShouldBe([b.AgencyId], "the lines carry the agency of their invoice");
    }

    [Fact]
    public async Task The_cash_sessions_and_expenses_of_an_agency_are_its_own_and_numbered_in_its_own_series()
    {
        var (a, b) = (await FinanceData.NewAgencyAsync(host, Ct), await FinanceData.NewAgencyAsync(host, Ct));
        var sessionA = await FinanceData.OpenSessionAsync(host, FinanceData.StaffOf(a, Roles.Agent), a, 100m, Ct);
        var sessionB = await FinanceData.OpenSessionAsync(host, FinanceData.StaffOf(b, Roles.Agent), b, 150m, Ct);
        var today = TunisTime.Today(TimeProvider.System);
        var expenseA = Ok(await RunAsync<ExpenseService, Result<Guid>>(host, FinanceData.StaffOf(a, Roles.Accountant), s => s.RecordAsync(Fuel(a, today), Ct)));
        var expenseB = Ok(await RunAsync<ExpenseService, Result<Guid>>(host, FinanceData.StaffOf(b, Roles.Accountant), s => s.RecordAsync(Fuel(b, today), Ct)));

        var accountantA = FinanceData.StaffOf(a, Roles.Accountant);
        var sessions = Ok(await RunAsync<ICashSessions, Result<CashSessionList>>(host, accountantA, s => s.ListAsync(new CashSessionFilter(), new PageRequest(1, 100), Ct)));
        sessions.Page.Items.Select(s => s.Id).ShouldBe([sessionA]);
        (await RunAsync<ICashSessions, Result<CashSessionDetails>>(host, accountantA, s => s.GetAsync(sessionB, Ct))).Error.ShouldBe(CashErrors.SessionNotFound);
        (await RunAsync<ICashSessions, Result<Guid>>(host, accountantA, s => s.AddManualMovementAsync(sessionB, new ManualCashMovementRequest(CashFlowDirectionInfo.In, 10m, "Intrusion"), Ct)))
            .Error.ShouldBe(CashErrors.SessionNotFound);
        (await RunAsync<ICashSessions, Result<Guid>>(host, FinanceData.StaffOf(a, Roles.Agent), s => s.OpenAsync(new OpenCashSessionRequest(b, 10m), Ct)))
            .Error.ShouldBe(CashErrors.AgencyNotFound);

        var listed = Ok(await RunAsync<ExpenseService, Result<ExpenseList>>(host, accountantA, s => s.ListAsync(new ExpenseFilter(b), new PageRequest(1, 100), Ct)));
        listed.Page.Items.ShouldBeEmpty();
        (await RunAsync<ExpenseService, Result<ExpenseView>>(host, accountantA, s => s.GetAsync(expenseB, Ct))).Error.ShouldBe(ExpenseErrors.NotFound);
        (await RunAsync<ExpenseService, Result<Guid>>(host, accountantA, s => s.RecordAsync(Fuel(b, today), Ct))).Error.ShouldBe(ExpenseErrors.AgencyNotFound);
        (await RunAsync<ExpenseService, Result>(host, accountantA, s => s.DeleteAsync(expenseB, "Saisie en double", Ct))).Error.ShouldBe(ExpenseErrors.NotFound);

        // The numbers of the expenses are per agency: the first of each agency is number 1 of the year.
        var numberA = Ok(await RunAsync<ExpenseService, Result<ExpenseView>>(host, accountantA, s => s.GetAsync(expenseA, Ct))).Number;
        var numberB = Ok(await RunAsync<ExpenseService, Result<ExpenseView>>(host, FinanceData.StaffOf(b, Roles.Accountant), s => s.GetAsync(expenseB, Ct))).Number;
        numberA.ShouldBe(numberB);
        await using var check = host.CreateScope();
        var db = check.ServiceProvider.GetRequiredService<IAppDbContext>();
        (await db.Set<Expense>().CountAsync(e => e.AgencyId == b, Ct)).ShouldBe(1, "nothing was recorded for the other agency");
        (await db.Set<CashSession>().SingleAsync(s => s.Id == sessionB, Ct)).Movements.ShouldAllBe(m => m.AgencyId == b);
    }

    [Fact]
    public async Task Each_agency_keeps_its_own_chart_of_accounts_and_journals_and_exports_its_facts_only()
    {
        var (a, b) = (await FinanceData.NewAgencyAsync(host, Ct), await FinanceData.NewAgencyAsync(host, Ct));
        var (fileA, fileB) = (await TestBookings.NewAsync(host, a, Ct), await TestBookings.NewAsync(host, b, Ct));
        Ok(await RunAsync<ICounterPayments, Result<Guid>>(host, FinanceData.StaffOf(a, Roles.Agent), p => p.RecordPaymentAsync(new CounterPaymentRequest(fileA, a, PaymentMethodInfo.CardTerminal, 100m, "TPE-A"), Ct)));
        Ok(await RunAsync<ICounterPayments, Result<Guid>>(host, FinanceData.StaffOf(b, Roles.Agent), p => p.RecordPaymentAsync(new CounterPaymentRequest(fileB, b, PaymentMethodInfo.CardTerminal, 200m, "TPE-B"), Ct)));
        var (accountantA, accountantB) = (FinanceData.StaffOf(a, Roles.Accountant), FinanceData.StaffOf(b, Roles.Accountant));

        // Each chart is provisioned for its agency and changed by its accountant only; a number is unique within an agency.
        var bankA = Ok(await RunAsync<ChartOfAccountsService, Result<IReadOnlyList<AccountView>>>(host, accountantA, c => c.ListAccountsAsync(Ct))).Single(x => x.Number == "532");
        var bankB = Ok(await RunAsync<ChartOfAccountsService, Result<IReadOnlyList<AccountView>>>(host, accountantB, c => c.ListAccountsAsync(Ct))).Single(x => x.Number == "532");
        bankA.Id.ShouldNotBe(bankB.Id);
        Ok(await RunAsync<ChartOfAccountsService, Result>(host, accountantA, c => c.RenameAccountAsync(bankA.Id, "Banque de l'agence A", Ct)));
        Ok(await RunAsync<ChartOfAccountsService, Result<Guid>>(host, accountantA, c => c.CreateAccountAsync("70611", "Locations courte durée", Ct)));
        Ok(await RunAsync<ChartOfAccountsService, Result>(host, accountantA, c => c.MapAsync(AccountingMappingCatalog.SalesRental, "70611", Ct)));
        Ok(await RunAsync<ChartOfAccountsService, Result<Guid>>(host, accountantB, c => c.CreateAccountAsync("70611", "Locations longue durée", Ct)));
        (await RunAsync<ChartOfAccountsService, Result>(host, accountantA, c => c.RenameAccountAsync(bankB.Id, "Intrusion", Ct))).Error.ShouldBe(AccountingErrors.AccountNotFound);

        var chartB = Ok(await RunAsync<ChartOfAccountsService, Result<IReadOnlyList<AccountView>>>(host, accountantB, c => c.ListAccountsAsync(Ct)));
        chartB.Single(x => x.Number == "532").Label.ShouldBe("Banque");
        chartB.Single(x => x.Number == "70611").Label.ShouldBe("Locations longue durée");
        Ok(await RunAsync<ChartOfAccountsService, Result<IReadOnlyList<MappingView>>>(host, accountantB, c => c.ListMappingAsync(Ct)))
            .Single(m => m.Key == AccountingMappingCatalog.SalesRental).AccountNumber.ShouldBe("7061");

        // The journals of an agency are generated from its facts only, and asked for by its own staff only.
        var month = AccountingMonth.Of(TunisTime.Today(TimeProvider.System));
        var journalsA = Ok(await RunAsync<JournalService, Result<JournalMonthView>>(host, accountantA, j => j.GetMonthAsync(a, month, cancellationToken: Ct)));
        journalsA.TotalDebit.ShouldBe(Money.Tnd(100m));
        journalsA.Balances.Single(x => x.AccountNumber == "532").AccountLabel.ShouldBe("Banque de l'agence A");
        (await RunAsync<JournalService, Result<JournalMonthView>>(host, accountantA, j => j.GetMonthAsync(b, month, cancellationToken: Ct))).Error.ShouldBe(AccountingErrors.AgencyNotFound);
        (await RunAsync<MonthlyClosingService, Result<Guid>>(host, accountantA, s => s.CloseAsync(b, month.Previous(), [], Ct))).Error.ShouldBe(AccountingErrors.AgencyNotFound);
        (await RunAsync<ReconciliationService, Result<Guid>>(
                host,
                accountantA,
                s => s.AddManualLineAsync(b, new StatementLineForm(TunisTime.Today(TimeProvider.System), "INTRUS", StatementLineKind.Payment, 10m, null, null, null), Ct)))
            .Error.ShouldBe(AccountingErrors.AgencyNotFound);
        (await RunAsync<ReconciliationService, Result<CashReconciliationView>>(host, accountantA, s => s.GetCashAsync(b, month, Ct))).Error.ShouldBe(AccountingErrors.AgencyNotFound);

        // The export of an agency over HTTP: the journals of the agency of the session only.
        (await ExportAsync(a, month)).ShouldBe("TOTAL;;;;;100.000;100.000;");
        (await ExportAsync(b, month)).ShouldBe("TOTAL;;;;;200.000;200.000;");
    }

    [Fact]
    public async Task The_payments_page_of_a_booking_of_another_agency_shows_an_unknown_booking()
    {
        var (a, b) = (await FinanceData.NewAgencyAsync(host, Ct), await FinanceData.NewAgencyAsync(host, Ct));
        var (bookingA, bookingB) = (await TestBookings.NewAsync(host, a, Ct), await TestBookings.NewAsync(host, b, Ct));
        using var client = host.CreateClient();
        await HttpSignIn.SignInWithTwoFactorAsync(client, (await TestStaff.CreateAsync(host, Roles.Agent, agencyId: a)).Email, "/admin", Ct);

        var own = await client.GetStringAsync(new Uri($"/admin/payments/bookings/{bookingA}", UriKind.Relative), Ct);
        var other = await client.GetStringAsync(new Uri($"/admin/payments/bookings/{bookingB}", UriKind.Relative), Ct);

        own.ShouldContain("data-testid=\"booking-payments\"");
        other.ShouldNotContain("data-testid=\"booking-payments\"", Case.Sensitive, "no panel to record money for another agency's booking");
        other.ShouldContain("Ce dossier de location n");
    }

    [Fact]
    public async Task An_agency_without_the_cash_module_takes_no_cash_and_says_why()
    {
        var agencyId = await FinanceData.NewAgencyAsync(host, Ct);
        var agent = FinanceData.StaffOf(agencyId, Roles.Agent);
        await FinanceData.OpenSessionAsync(host, agent, agencyId, 100m, Ct);
        await FinanceData.SetModulesAsync(host, agencyId, AgencyModuleCatalog.Defaults & ~AgencyModules.Cash, Ct);
        var bookingId = await TestBookings.NewAsync(host, agencyId, Ct);

        (await RunAsync<ICounterPayments, Result<Guid>>(host, agent, p => p.RecordPaymentAsync(new CounterPaymentRequest(bookingId, agencyId, PaymentMethodInfo.Cash, 80m), Ct)))
            .Error.ShouldBe(CashErrors.ModuleOff);
        (await RunAsync<IDeposits, Result<Guid>>(host, agent, d => d.TakeAsync(new DepositRequest(bookingId, agencyId, DepositModeInfo.Cash, 300m), Ct)))
            .Error.ShouldBe(CashErrors.ModuleOff);
        Ok(await RunAsync<ICounterPayments, Result<Guid>>(host, agent, p => p.RecordPaymentAsync(new CounterPaymentRequest(bookingId, agencyId, PaymentMethodInfo.CardTerminal, 80m, "TPE-1"), Ct)));
    }

    private static ExpenseForm Fuel(Guid agencyId, DateOnly day) =>
        new(agencyId, day, ExpenseCategoryInfo.Fuel, "Station Agil", ExpenseAmountMode.InclVatAndRate, 59.5m, null, null, 0.19m, ExpensePaymentMethodInfo.Card, "TPE-RECU");

    /// <summary>The control line of the CSV export of the journals of the month, for an accountant of <paramref name="agencyId"/>.</summary>
    private async Task<string> ExportAsync(Guid agencyId, AccountingMonth month)
    {
        using var client = host.CreateClient();
        await HttpSignIn.SignInWithTwoFactorAsync(client, (await TestStaff.CreateAsync(host, Roles.Accountant, agencyId: agencyId)).Email, "/admin", Ct);
        var csv = await client.GetAsync(new Uri($"/admin/accounting/export?from={month}&format=csv", UriKind.Relative), Ct);
        csv.StatusCode.ShouldBe(HttpStatusCode.OK);
        var rows = Encoding.UTF8.GetString(await csv.Content.ReadAsByteArrayAsync(Ct)).TrimStart('﻿').Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        return rows[^1];
    }
}
