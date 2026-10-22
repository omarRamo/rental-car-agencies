using System.Globalization;
using System.Text;
using CarRental.Application.Accounting.Closing;
using CarRental.Application.Accounting.Contracts;
using CarRental.Application.Accounting.Journals;
using CarRental.Application.Accounting.Reconciliation;
using CarRental.Application.Billing.Contracts;
using CarRental.Application.Billing.Fines;
using CarRental.Application.Billing.Invoicing;
using CarRental.Application.Bookings;
using CarRental.Application.Cash.Contracts;
using CarRental.Application.Cash.Expenses;
using CarRental.Application.Channels.Administration;
using CarRental.Application.Channels.Contracts;
using CarRental.Application.Common.Security;
using CarRental.Application.Fleet.Contracts;
using CarRental.Application.Identity.Contracts;
using CarRental.Application.Payments.Contracts;
using CarRental.Application.Reporting.Indicators;
using CarRental.Application.Reporting.Snapshots;
using CarRental.Domain.Accounting;
using CarRental.Domain.Billing;
using CarRental.Domain.Common;
using CarRental.Infrastructure.Persistence;
using CarRental.Integration.Tests.Billing;
using CarRental.Integration.Tests.Payments;
using CarRental.Integration.Tests.PublicBooking;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static CarRental.Integration.Tests.Bookings.BookingWorld;
using static CarRental.Integration.Tests.Bookings.Handover.HandoverWorld;

namespace CarRental.Integration.Tests.Accounting;

/// <summary>
/// The "done when" of package 3B (ADR-0063 to ADR-0065): a month of an agency simulated through the real services on SQL
/// Server and Azurite — counter and web rentals, cash, card, transfer, cheque and Clic to Pay (captured, then partly
/// refunded) payments, deposits released, retained for the return charges and still held at the end of the month,
/// invoices at the closing of the files, a cancellation fee, a credit note, a fine re-invoiced, expenses paid in cash and by
/// card (one of a vehicle), a Booking.com booking prepaid to the channel with its commission and payout — then its journals
/// generated and exported, checked balanced and against the facts, the reports and the CSV and Excel exports, the month
/// closed and every late write refused.
/// </summary>
[Collection(AccountingMonthCollection.Name)]
public sealed class AccountingMonthSimulationTests(AccountingMonthHost month)
{
    private static readonly AccountingMonth March = AccountingMonth.Create(2027, 3).Value;

    private static readonly Dictionary<InvoiceLineNatureInfo, string> SalesAccounts = new()
    {
        [InvoiceLineNatureInfo.Rental] = "7061",
        [InvoiceLineNatureInfo.Discount] = "709",
        [InvoiceLineNatureInfo.Extras] = "7062",
        [InvoiceLineNatureInfo.Protection] = "7063",
        [InvoiceLineNatureInfo.Delivery] = "7064",
        [InvoiceLineNatureInfo.DailyContribution] = "4367",
        [InvoiceLineNatureInfo.ExtraMileage] = "7065",
        [InvoiceLineNatureInfo.Fuel] = "7066",
        [InvoiceLineNatureInfo.LateReturn] = "7067",
        [InvoiceLineNatureInfo.Cleaning] = "7068",
        [InvoiceLineNatureInfo.OtherCharge] = "7069",
        [InvoiceLineNatureInfo.Damage] = "7581",
        [InvoiceLineNatureInfo.CancellationFee] = "7582",
        [InvoiceLineNatureInfo.FineReinvoicing] = "4671",
        [InvoiceLineNatureInfo.ManagementFee] = "7583",
    };

    private static readonly Dictionary<ExpenseCategoryInfo, string> ExpenseAccounts = new()
    {
        [ExpenseCategoryInfo.Fuel] = "6061",
        [ExpenseCategoryInfo.Utilities] = "6065",
    };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DateOnly Day(int day) => new(2027, 3, day);

    [Fact]
    public async Task A_simulated_month_gives_balanced_journals_matching_the_facts_the_exports_and_the_reports_then_closes()
    {
        var host = month.Host;
        var clock = month.Clock;
        var world = new AccountingMonthWorld(host, clock, Ct);
        var agent = FinanceData.Actor(Roles.Agent);
        var accountant = FinanceData.Actor(Roles.Accountant);
        var manager = FinanceData.Actor(Roles.Manager);

        // 1 March: the agency, its seller identity, its Booking.com channel.
        clock.SetTunis(Day(1), new TimeOnly(8, 0));
        var agency = await world.AgencyAsync();

        // 3 March: a counter rental paid in cash in the session of the agent, deposit by card imprint; the drawer is
        // counted 1.000 short in the evening (justified by the cashier) and locked by the manager.
        clock.SetTunis(Day(3), new TimeOnly(8, 30));
        var agentSession = await world.OpenSessionAsync(agent, agency, 100m);
        clock.SetTunis(Day(3), new TimeOnly(10, 0));
        var b1 = await world.FileAsync(agency, agency.Vehicle(0), days: 2);
        var b1Deposit = Guid.Empty;
        await world.DepartAsync(agent, b1, async () =>
        {
            await world.PayAsync(agent, agency, b1.BookingId, PaymentMethodInfo.Cash, b1.Total);
            b1Deposit = await world.TakeDepositAsync(agent, b1, DepositModeInfo.CardImprint, "AUT-B1-0303");
        });
        clock.SetTunis(Day(3), new TimeOnly(18, 0));
        var counted = await world.CountAndLockAsync(agent, manager, agentSession, 100m + b1.Total.Amount - 1m, "Erreur de rendu de monnaie");
        counted.RevenueVariance.ShouldBe(Money.Tnd(-1m));

        // 5 March: its return, the imprint released, the file closed and invoiced.
        clock.SetTunis(Day(5), new TimeOnly(9, 30));
        (await world.ReturnAsync(agent, b1)).ChargesTotal.ShouldBe(Money.Zero);
        Ok(await RunAsync<IDeposits, Result>(host, agent, d => d.ReleaseAsync(b1Deposit, Ct)));
        var b1Invoice = await world.CloseAsync(agent, b1);

        // 8 March: a web request, its guarantee pre-authorized on Clic to Pay, captured at the validation.
        clock.SetTunis(Day(8), new TimeOnly(9, 0));
        var web = await FunnelWorld.SubmitAsync(host, agency.Agency, TunisTime.FromLocal(Day(12), new TimeOnly(10, 0)), TunisTime.FromLocal(Day(14), new TimeOnly(10, 0)), Ct);
        var guarantee = await FunnelWorld.HoldGuaranteeAsync(host, web, Ct);
        Ok(await FunnelWorld.ValidateAsync(host, web.BookingId, Ct)).ShouldBe(ValidationOutcome.Confirmed);

        // 10 March: the accountant pays the electricity in cash from a session of her own, counted exactly.
        clock.SetTunis(Day(10), new TimeOnly(9, 0));
        var accountantSession = await world.OpenSessionAsync(accountant, agency, 50m);
        var electricity = Ok(await RunAsync<ExpenseService, Result<Guid>>(host, accountant, s => s.RecordAsync(
            new ExpenseForm(agency.AgencyId, Day(10), ExpenseCategoryInfo.Utilities, "STEG", ExpenseAmountMode.InclVatAndRate, 23.8m, null, null, 0.19m, ExpensePaymentMethodInfo.Cash, Reference: "STEG-0310"),
            Ct)));
        (await world.CountAndLockAsync(accountant, manager, accountantSession, 26.2m)).RevenueVariance.ShouldBe(Money.Zero);

        // 12 March: a Booking.com reservation prepaid to the channel for the 20th; the web rental leaves (prepaid online,
        // any balance at the card terminal), a card imprint taken.
        clock.SetTunis(Day(12), new TimeOnly(8, 0));
        var channelReference = "BKG-" + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();
        var channelGross = b1.Total;
        var channelCommission = Money.Tnd(30m);
        var k = await world.ChannelReservationAsync(agency, channelReference, TunisTime.FromLocal(Day(20), new TimeOnly(10, 0)), 2, channelGross, channelCommission);
        clock.SetTunis(Day(12), new TimeOnly(10, 10));
        Ok(await RunAsync<VehicleAssignmentService, Result>(host, Agent, s => s.AssignAsync(web.BookingId, agency.Vehicle(1), cancellationToken: Ct)));
        var w = await world.FileOfAsync(agency, web.BookingId, web.Email);
        var wDeposit = Guid.Empty;
        await world.DepartAsync(agent, w, async () =>
        {
            var paid = Ok(await RunAsync<ICounterPayments, Result<BookingPayments>>(host, agent, p => p.GetBookingPaymentsAsync(w.BookingId, Ct)));
            paid.Totals.NetPaid.ShouldBe(guarantee.Amount, "the guarantee captured at the validation");
            if (w.Total > paid.Totals.NetPaid)
            {
                await world.PayAsync(agent, agency, w.BookingId, PaymentMethodInfo.CardTerminal, w.Total - paid.Totals.NetPaid, "TPE-W-0312");
            }

            wDeposit = await world.TakeDepositAsync(agent, w, DepositModeInfo.CardImprint, "AUT-W-0312");
        });

        // 14 March: the web rental comes back; 15 March: a booking cancelled two days before its pick-up (20 % fee,
        // invoiced and paid by cheque).
        clock.SetTunis(Day(14), new TimeOnly(9, 30));
        (await world.ReturnAsync(agent, w)).ChargesTotal.ShouldBe(Money.Zero);
        Ok(await RunAsync<IDeposits, Result>(host, agent, d => d.ReleaseAsync(wDeposit, Ct)));
        var wInvoice = await world.CloseAsync(agent, w);
        clock.SetTunis(Day(15), new TimeOnly(10, 0));
        var cancelled = await world.FileAsync(agency, agency.Vehicle(2), days: 2, SalesChannel.Phone, pickupIn: TimeSpan.FromDays(2));
        var fee = Ok(await RunAsync<BookingWorkflowService, Result<Money>>(host, Agent, s => s.CancelAsync(cancelled.BookingId, "Voyage annulé", cancellationToken: Ct)));
        fee.IsPositive.ShouldBeTrue("cancelled two days before the pick-up: the fee of the policy");
        var feeInvoice = await InvoiceAsync(cancelled.BookingId, InvoiceKind.CancellationFee);
        await world.PayAsync(agent, agency, cancelled.BookingId, PaymentMethodInfo.Cheque, feeInvoice.TotalDue, "CHQ-0315");

        // 16 March: a commercial gesture on the web rental — 20.000 refunded on Clic to Pay and credited.
        clock.SetTunis(Day(16), new TimeOnly(10, 0));
        var refund = Ok(await RunAsync<IOnlinePayments, Result<OnlineRefundOutcome>>(host, agent, p => p.RefundAsync(new OnlineRefundRequest(guarantee.PaymentId, 20m, "Geste commercial"), Ct)));
        refund.State.ShouldBe(OnlineRefundStateInfo.Refunded);
        var rentalLine = wInvoice.Lines.Where(l => l.Nature == InvoiceLineNature.Rental).OrderByDescending(l => l.InclVat).First();
        var creditNote = Ok(await RunAsync<InvoicingService, Result<IssuedDocument>>(host, accountant, s => s.CreateCreditNoteAsync(
            wInvoice.Id,
            new CreditNoteRequest(CreditNoteScope.Partial, "Geste commercial", [new CreditLineRequest(rentalLine.Id, 20m)]),
            Ct)));

        // 18 March: fuel for the vehicle of the web rental, paid by card, with its receipt.
        clock.SetTunis(Day(18), new TimeOnly(11, 0));
        var fuel = Ok(await RunAsync<ExpenseService, Result<Guid>>(host, accountant, s => s.RecordAsync(
            new ExpenseForm(agency.AgencyId, Day(18), ExpenseCategoryInfo.Fuel, "Station Agil Lac", ExpenseAmountMode.InclVatAndRate, 119m, null, null, 0.19m, ExpensePaymentMethodInfo.Card, "TPE-AGIL-18", VehicleId: agency.Vehicle(1)),
            Ct)));
        Ok(await RunAsync<ExpenseService, Result>(host, accountant, s => s.UploadReceiptAsync(fuel, "ticket.jpg", new MemoryStream(Jpeg), Ct)));

        // 20 March: the Booking.com customer arrives, prepaid to the channel; a fine of the 4th on the counter rental is
        // matched with it, designated and re-invoiced with the management fee.
        clock.SetTunis(Day(20), new TimeOnly(10, 0));
        Ok(await RunAsync<VehicleAssignmentService, Result>(host, Agent, s => s.AssignAsync(k, agency.Vehicle(4), cancellationToken: Ct)));
        await world.PayAsync(agent, agency, k, PaymentMethodInfo.ChannelPrepaid, channelGross, channelReference);
        clock.SetTunis(Day(20), new TimeOnly(11, 0));
        var plate = (await RunAsync<IFleetQueries, VehicleInfo?>(host, agent, q => q.GetVehicleAsync(b1.VehicleId, Ct))).ShouldNotBeNull().PlateNumber;
        var ticket = $"PV{Guid.NewGuid():N}"[..18].ToUpperInvariant();
        var fineId = Ok(await RunAsync<FineService, Result<Guid>>(host, agent, s => s.RecordAsync(
            new FineForm(agency.AgencyId, ticket, "Police de la circulation", TunisTime.FromLocal(Day(4), new TimeOnly(15, 0)), "Avenue Habib Bourguiba, Tunis", plate.Replace(" ", string.Empty, StringComparison.Ordinal), 60m),
            Ct)));
        Ok(await RunAsync<FineService, Result<FineDesignationFile>>(host, agent, s => s.ProduceDesignationFormAsync(fineId, Ct)));
        Ok(await RunAsync<FineService, Result>(host, agent, s => s.MarkDesignatedAsync(fineId, Ct)));
        var fineInvoice = Ok(await RunAsync<FineService, Result<IssuedDocument>>(host, agent, s => s.ReinvoiceAsync(fineId, Ct)));

        // 24-26 March: a rental paid at the card terminal, back with two eighths of fuel missing and to clean: the charges
        // are retained from its card imprint and invoiced with the rental.
        clock.SetTunis(Day(24), new TimeOnly(10, 0));
        var b2 = await world.FileAsync(agency, agency.Vehicle(2), days: 2);
        var b2Deposit = Guid.Empty;
        await world.DepartAsync(agent, b2, async () =>
        {
            await world.PayAsync(agent, agency, b2.BookingId, PaymentMethodInfo.CardTerminal, b2.Total, "TPE-B2-0324");
            b2Deposit = await world.TakeDepositAsync(agent, b2, DepositModeInfo.CardImprint, "AUT-B2-0324");
        });
        clock.SetTunis(Day(26), new TimeOnly(9, 30));
        var charges = (await world.ReturnAsync(agent, b2, fuelEighths: 6, needsCleaning: true)).ChargesTotal;
        charges.IsPositive.ShouldBeTrue();
        Ok(await RunAsync<IDeposits, Result>(host, agent, d => d.RetainAsync(b2Deposit, new DepositRetentionRequest(charges.Amount, "Carburant manquant et nettoyage"), Ct)));
        var b2Invoice = await world.CloseAsync(agent, b2);

        // 30 March: a rental that runs into April, paid by transfer, its deposit a cheque still held at the end of the month.
        clock.SetTunis(Day(30), new TimeOnly(10, 0));
        var r3 = await world.FileAsync(agency, agency.Vehicle(3), days: 3);
        await world.DepartAsync(agent, r3, async () =>
        {
            await world.PayAsync(agent, agency, r3.BookingId, PaymentMethodInfo.BankTransfer, r3.Total, "VIR-R3-0330");
            await world.TakeDepositAsync(agent, r3, DepositModeInfo.Cheque, "CHQ-R3-0330");
        });

        // 31 March: the Booking.com payout (what the channel should pay) and the Clic to Pay statement of the month.
        clock.SetTunis(Day(31), new TimeOnly(15, 0));
        var payout = channelGross - channelCommission;
        Ok(await RunAsync<ChannelReconciliationService, Result>(host, manager, s => s.RecordPayoutAsync(agency.Channel.Id, 2027, 3, payout.Amount, "VIR-BKG-0327", null, Ct)));
        clock.SetTunis(Day(31), new TimeOnly(16, 0));
        var statement = $"""
            date;reference;type;amount;fee;settlement_date
            2027-03-08;{guarantee.OrderNumber};payment;{Csv(guarantee.Amount)};2.500;2027-03-10
            2027-03-16;{guarantee.OrderNumber};refund;20.000;;2027-03-17
            """;
        var imported = Ok(await RunAsync<ReconciliationService, Result<StatementImportOutcome>>(host, accountant, s => s.ImportStatementAsync(
            agency.AgencyId, GenericCsvStatementImporter.FormatName, "releve-clictopay-2027-03.csv", new MemoryStream(Encoding.UTF8.GetBytes(statement)), Ct)));
        imported.IsImported.ShouldBeTrue();

        // 2 April: the month is over. Its journals, generated from the live facts.
        clock.SetTunis(new DateOnly(2027, 4, 2), new TimeOnly(9, 0));
        var journals = Ok(await RunAsync<JournalService, Result<JournalMonthView>>(host, accountant, s => s.GetMonthAsync(agency.AgencyId, March, cancellationToken: Ct)));
        journals.IsComplete.ShouldBeTrue(string.Join(", ", journals.MissingKeys.Concat(journals.Errors)));
        journals.IsClosed.ShouldBeFalse();
        journals.IsBalanced.ShouldBeTrue();
        journals.Entries.ShouldAllBe(e => e.TotalDebit == e.TotalCredit && March.Contains(e.Date));
        journals.TotalDebit.ShouldBe(journals.TotalCredit);
        journals.Totals.ShouldAllBe(t => t.Debit == t.Credit);

        // Every account against the facts of the other modules.
        var facts = await FactsAsync(agency.AgencyId, accountant);
        var expected = Expected(facts);
        var balances = journals.Balances.ToDictionary(b => b.AccountNumber, b => b.Balance, StringComparer.Ordinal);
        foreach (var (account, amount) in expected)
        {
            balances.GetValueOrDefault(account).ShouldBe(amount, $"account {account}");
        }

        balances.Where(b => !b.Value.IsZero).Select(b => b.Key).ShouldBeSubsetOf(expected.Keys);
        expected["165"].ShouldBe(-r3.Deposit, "the cheque of the rental of April is the only deposit still owed");
        expected["4118"].ShouldBe(Money.Zero, "the payout settled what Booking.com collected");
        expected["5811"].ShouldBe(Money.Zero, "the bank paid every Clic to Pay line of the month");
        expected["6226"].ShouldBe(channelCommission);
        expected["6061"].ShouldBe(Money.Tnd(100m));

        // The pieces and the lettering: one entry per document, the customer lines lettered with their file.
        var sales = journals.Entries.Where(e => e.Journal == Journal.Sales).ToList();
        sales.Select(e => e.Piece).ShouldBe(facts.Sales.Select(d => d.Number), ignoreOrder: true);
        sales.Select(e => e.Piece).ShouldContain(creditNote.Number);
        sales.Select(e => e.Piece).ShouldContain(fineInvoice.Number);
        journals.Entries.Where(e => e.Journal == Journal.Purchases).Select(e => e.Piece).ShouldBe(facts.Expenses.Select(x => x.Number), ignoreOrder: true);
        journals.Entries.SelectMany(e => e.Lines).Where(l => l.AccountNumber == "411").ShouldAllBe(l => l.LetteringKey != null && l.LetteringKey.StartsWith("LOC-", StringComparison.Ordinal));
        journals.Entries.SelectMany(e => e.Lines).Where(l => l.AccountNumber == "401").ShouldAllBe(l => l.LetteringKey != null && l.LetteringKey.StartsWith("DEP-", StringComparison.Ordinal));
        journals.Entries.Where(e => e.SourceKey.StartsWith("deposit:", StringComparison.Ordinal)).SelectMany(e => e.Lines).ShouldNotContain(l => l.AccountNumber.StartsWith('7'), "a deposit is never revenue (invariant 8)");

        // The CSV and the Excel workbook give the same lines, with the same control totals.
        var csv = Ok(await RunAsync<JournalService, Result<JournalFile>>(host, accountant, s => s.ExportAsync(agency.AgencyId, March, March, null, "csv", Ct)));
        var xlsx = Ok(await RunAsync<JournalService, Result<JournalFile>>(host, accountant, s => s.ExportAsync(agency.AgencyId, March, March, null, "xlsx", Ct)));
        var csvLines = CsvLines(csv.Content);
        var xlsxLines = WorkbookLines(xlsx.Content, out var xlsxTotals);
        csvLines.Count.ShouldBe(journals.Entries.Sum(e => e.Lines.Count));
        xlsxLines.ShouldBe(csvLines);
        xlsxTotals.ShouldBe((journals.TotalDebit.Amount, journals.TotalCredit.Amount));

        // The reports agree: invoiced, collected, deposits held, the costs of the vehicle.
        Ok(await RunAsync<SnapshotService, Result<SnapshotRun>>(host, manager, s => s.RecomputeAsync(agency.AgencyId, March.FirstDay, March.LastDay, Ct)));
        var report = Ok(await RunAsync<ReportQueries, Result<ReportDashboard>>(host, accountant, q => q.GetDashboardAsync(new ReportRequest(agency.AgencyId, March.FirstDay, March.LastDay), Ct)));
        report.Cash.Invoiced.ExclVat.ShouldBe(facts.Sales.Select(d => d.TotalExclVat).Sum());
        report.Cash.Invoiced.InclVat.ShouldBe(facts.Sales.Select(d => d.TotalInclVat).Sum());
        report.Cash.Invoiced.Vat.ShouldBe(-(balances.GetValueOrDefault("43661") + balances.GetValueOrDefault("43662") + balances.GetValueOrDefault("43663") + balances.GetValueOrDefault("4366")));
        (report.Cash.Invoiced.InclVat - balances.GetValueOrDefault("4368")).ShouldBe(sales.SelectMany(e => e.Lines).Where(l => l.AccountNumber == "411").Select(l => l.Debit - l.Credit).Sum());
        foreach (var method in facts.Payments.Where(p => p.Method != PaymentMethodInfo.ChannelPrepaid).GroupBy(p => p.Method))
        {
            var collected = report.Cash.Collected.Single(c => c.Method == method.Key);
            collected.Received.ShouldBe(method.Where(p => p.Direction == PaymentDirectionInfo.Incoming).Select(p => p.Amount).Sum(), method.Key.ToString());
            collected.Refunded.ShouldBe(method.Where(p => p.Direction == PaymentDirectionInfo.Refund).Select(p => p.Amount).Sum(), method.Key.ToString());
        }

        report.Cash.OpenDeposits.ShouldBe(-balances["165"]);
        report.Vehicles.Single(v => v.VehicleId == agency.Vehicle(1)).Totals.DirectCosts.ShouldBe(balances["6061"], "the fuel of the vehicle, counted once, excluding VAT");

        // The closing: refused while the counted difference of the 3rd is not justified, then done.
        var closing = Ok(await RunAsync<MonthlyClosingService, Result<ClosingPreview>>(host, accountant, s => s.PreviewAsync(agency.AgencyId, March, Ct)));
        closing.Checks.Select(c => c.Kind).ShouldBe([ClosingCheckKind.UnjustifiedDifference], string.Join(", ", closing.Checks.Select(c => $"{c.Kind} {c.Subject}")));
        (await RunAsync<MonthlyClosingService, Result<Guid>>(host, accountant, s => s.CloseAsync(agency.AgencyId, March, [], Ct))).Error.ShouldBe(AccountingErrors.ClosingBlocked);
        Ok(await RunAsync<ReconciliationService, Result<Guid>>(host, accountant, s => s.JustifyAsync(agency.AgencyId, March, $"cash:variance:{agentSession:N}:revenue", "Erreur de rendu de monnaie constatée par le caissier", Ct)));
        var ready = Ok(await RunAsync<MonthlyClosingService, Result<ClosingPreview>>(host, accountant, s => s.PreviewAsync(agency.AgencyId, March, Ct)));
        ready.CanClose.ShouldBeTrue(string.Join(", ", ready.Checks.Select(c => $"{c.Kind} {c.Subject}")));
        Ok(await RunAsync<MonthlyClosingService, Result<Guid>>(host, accountant, s => s.CloseAsync(agency.AgencyId, March, ready.ConfirmableKeys, Ct)));

        // Frozen: the same lines whatever is exported later; the closed month in the list of the months.
        var frozen = Ok(await RunAsync<JournalService, Result<JournalMonthView>>(host, accountant, s => s.GetMonthAsync(agency.AgencyId, March, cancellationToken: Ct)));
        frozen.IsClosed.ShouldBeTrue();
        frozen.Entries.Count.ShouldBe(journals.Entries.Count);
        frozen.Balances.Select(b => (b.AccountNumber, b.Balance)).ShouldBe(journals.Balances.Select(b => (b.AccountNumber, b.Balance)));
        Ok(await RunAsync<JournalService, Result<JournalFile>>(host, accountant, s => s.ExportAsync(agency.AgencyId, March, March, null, "csv", Ct))).Content.ShouldBe(csv.Content);
        var periods = Ok(await RunAsync<MonthlyClosingService, Result<IReadOnlyList<PeriodView>>>(host, accountant, s => s.ListPeriodsAsync(agency.AgencyId, 2, Ct)));
        periods.Single(p => p.Month == March).ShouldSatisfyAllConditions(
            p => p.Status.ShouldBe(AccountingPeriodStatus.Closed),
            p => p.EntryCount.ShouldBe(journals.Entries.Count),
            p => p.TotalDebit.ShouldBe(journals.TotalDebit));

        // Late writes: nothing more is dated in March.
        (await RunAsync<ExpenseService, Result<Guid>>(host, accountant, s => s.RecordAsync(
            new ExpenseForm(agency.AgencyId, Day(28), ExpenseCategoryInfo.Fuel, "Station Agil Lac", ExpenseAmountMode.InclVatAndRate, 59.5m, null, null, 0.19m, ExpensePaymentMethodInfo.Card, "TPE-LATE"),
            Ct))).Error.ShouldBe(AccountingErrors.PeriodClosed);
        (await RunAsync<ExpenseService, Result>(host, accountant, s => s.DeleteAsync(fuel, "Saisie en double", Ct))).Error.ShouldBe(AccountingErrors.PeriodClosed);
        (await RunAsync<ReconciliationService, Result<Guid>>(host, accountant, s => s.AddManualLineAsync(
            agency.AgencyId, new StatementLineForm(Day(29), "LATE-1", StatementLineKind.Payment, 10m, null, null, null), Ct))).Error.ShouldBe(AccountingErrors.PeriodClosed);
        clock.SetTunis(Day(31), new TimeOnly(23, 0));
        (await RunAsync<ICounterPayments, Result<Guid>>(host, agent, p => p.RecordPaymentAsync(
            new CounterPaymentRequest(r3.BookingId, agency.AgencyId, PaymentMethodInfo.CardTerminal, 10m, "TPE-LATE"), Ct))).Error.ShouldBe(AccountingErrors.PeriodClosed);
        (await RunAsync<ICashSessions, Result<Guid>>(host, agent, s => s.OpenAsync(new OpenCashSessionRequest(agency.AgencyId, 50m), Ct))).Error.ShouldBe(AccountingErrors.PeriodClosed);
        (await RunAsync<IAccountingPeriods, IReadOnlyList<DateOnly>>(host, accountant, p => p.GetClosedMonthsAsync(agency.AgencyId, Ct))).ShouldBe([March.FirstDay]);

        // April stays open.
        clock.SetTunis(new DateOnly(2027, 4, 2), new TimeOnly(10, 30));
        await world.PayAsync(agent, agency, r3.BookingId, PaymentMethodInfo.CardTerminal, Money.Tnd(10m), "TPE-R3-0402");

        TestContext.Current.TestOutputHelper?.WriteLine(Summary(journals, facts, b1Invoice, wInvoice, b2Invoice, feeInvoice, electricity));
    }

    private static Dictionary<string, Money> Expected(MonthFacts facts)
    {
        var expected = new Dictionary<string, Money>(StringComparer.Ordinal);
        void Add(string account, Money amount) => expected[account] = expected.GetValueOrDefault(account) + amount;

        // Sales: the customer debited with what is due, each nature, VAT rate and the stamp duty credited.
        foreach (var document in facts.Sales)
        {
            Add("411", document.TotalDue);
            foreach (var line in document.Lines)
            {
                Add(SalesAccounts[line.Nature], line.ExclVat.Negate());
            }

            foreach (var vat in document.VatTotals.Where(v => !v.Vat.IsZero))
            {
                Add(vat.Rate switch { 0.19m => "43661", 0.13m => "43662", 0.07m => "43663", _ => "4366" }, vat.Vat.Negate());
            }

            Add("4368", document.StampDuty.Negate());
        }

        // Payments: the customer settled, the money on its treasury account (cash through the drawer, the channel owes
        // what it collected).
        foreach (var payment in facts.Payments)
        {
            var signed = payment.Direction == PaymentDirectionInfo.Incoming ? payment.Amount : payment.Amount.Negate();
            Add("411", signed.Negate());
            Add(payment.Method switch
            {
                PaymentMethodInfo.Cash => "541",
                PaymentMethodInfo.ClicToPay => "5811",
                PaymentMethodInfo.ChannelPrepaid => "4118",
                _ => "532",
            }, signed);
        }

        // Deposits (card imprints and cheques): a liability while held; the part retained settles the customer and is collected.
        foreach (var deposit in facts.Deposits.Where(d => d.Mode != DepositModeInfo.Cash))
        {
            var guarantee = deposit.Mode == DepositModeInfo.CardImprint ? "5812" : "5813";
            if (deposit.Kind == DepositMovementKindInfo.Taken)
            {
                Add(guarantee, deposit.Amount);
                Add("165", deposit.Amount.Negate());
            }
            else
            {
                Add("165", deposit.Amount);
                Add("411", deposit.Retained.Negate());
                Add(guarantee, deposit.Returned.Negate());
                Add("532", deposit.Retained);
                Add(guarantee, deposit.Retained.Negate());
            }
        }

        // Expenses: the charge and the deductible VAT, the supplier paid in cash or by bank.
        foreach (var expense in facts.Expenses)
        {
            Add(ExpenseAccounts[expense.Category], expense.ExclVat);
            Add("4365", expense.Vat);
            Add(expense.PaymentMethod == ExpensePaymentMethodInfo.Cash ? "541" : "532", expense.InclVat.Negate());
        }

        // The counted differences of the cash sessions.
        foreach (var variance in facts.Sessions.Select(s => s.RevenueVariance ?? Money.Zero).Where(v => !v.IsZero))
        {
            Add("541", variance);
            Add(variance.IsPositive ? "758" : "658", variance.Negate());
        }

        // The Booking.com payouts: the receivable settled by the payout and the commissions it deducts.
        foreach (var payout in facts.Payouts)
        {
            Add("532", payout.Payout!.Value);
            Add("6226", payout.DeductedCommission);
            Add("4118", payout.PrepaidGross.Negate());
            Add("471", (payout.Payout!.Value - payout.ExpectedSettlement).Negate());
        }

        // The Clic to Pay lines settled by the bank, less its fees.
        foreach (var line in facts.Statement.Where(l => l.SettledOn is not null))
        {
            Add("532", line.SignedAmount - (line.Fee ?? Money.Zero));
            Add("6271", line.Fee ?? Money.Zero);
            Add("5811", line.SignedAmount.Negate());
        }

        return expected;
    }

    private static string Csv(Money amount) => amount.Amount.ToString("0.000", CultureInfo.InvariantCulture);

    /// <summary>The lines of the generic CSV export (no header, no control total).</summary>
    private static List<string> CsvLines(byte[] content)
    {
        content[..3].ShouldBe(Encoding.UTF8.GetPreamble());
        var rows = Encoding.UTF8.GetString(content[3..]).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        rows[0].ShouldBe("date;journal;compte;piece;libelle;debit;credit;lettrage");
        rows[^1].ShouldStartWith("TOTAL;");
        return [.. rows[1..^1].Select(r => string.Join('|', Cells(r)))];
    }

    /// <summary>The lines of the generic Excel export in the same shape as <see cref="CsvLines"/>, and its control totals.</summary>
    private static List<string> WorkbookLines(byte[] content, out (decimal Debit, decimal Credit) totals)
    {
        using var workbook = new XLWorkbook(new MemoryStream(content));
        var sheet = workbook.Worksheet("Écritures");
        sheet.Cell(1, 1).GetString().ShouldBe("Date");
        var lines = new List<string>();
        var row = 2;
        for (; !sheet.Cell(row, 1).IsEmpty(); row++)
        {
            var date = DateOnly.FromDateTime(sheet.Cell(row, 1).GetDateTime()).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            string Text(int column) => sheet.Cell(row, column).GetString();
            string Amount(int column) => sheet.Cell(row, column).GetValue<decimal>().ToString("0.000", CultureInfo.InvariantCulture);
            lines.Add(string.Join('|', date, Text(2), Text(3), Text(4), Text(5), Amount(6), Amount(7), Text(8)));
        }

        totals = (sheet.Cell(row + 1, 6).GetValue<decimal>(), sheet.Cell(row + 1, 7).GetValue<decimal>());
        return lines;
    }

    /// <summary>Splits a row of the generic CSV (semicolons, double quotes doubled inside a quoted cell).</summary>
    private static List<string> Cells(string row)
    {
        var cells = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < row.Length; i++)
        {
            var c = row[i];
            if (quoted && c == '"' && i + 1 < row.Length && row[i + 1] == '"')
            {
                cell.Append('"');
                i++;
            }
            else if (c == '"')
            {
                quoted = !quoted;
            }
            else if (c == ';' && !quoted)
            {
                cells.Add(cell.ToString());
                cell.Clear();
            }
            else
            {
                cell.Append(c);
            }
        }

        cells.Add(cell.ToString());
        return cells;
    }

    private static string Summary(JournalMonthView journals, MonthFacts facts, params object[] documents)
    {
        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"Month {journals.Month}: {journals.Entries.Count} entries, {journals.Entries.Sum(e => e.Lines.Count)} lines, debit = credit = {journals.TotalDebit}");
        foreach (var total in journals.Totals)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"  {total.Code}: {total.EntryCount} entries, {total.Debit} / {total.Credit}");
        }

        text.AppendLine(CultureInfo.InvariantCulture, $"  {facts.Sales.Count} sales documents, {facts.Payments.Count} payments, {facts.Deposits.Count} deposit movements, {facts.Expenses.Count} expenses, {documents.Length} documents followed");
        foreach (var balance in journals.Balances)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"  {balance.AccountNumber,-6} {balance.Debit,14} {balance.Credit,14} {balance.Balance,14}");
        }

        return text.ToString();
    }

    private async Task<Invoice> InvoiceAsync(Guid bookingId, InvoiceKind kind) =>
        await BillingWorld.WaitForAsync(
            async () =>
            {
                await using var scope = month.Host.CreateScope();
                return await scope.ServiceProvider.GetRequiredService<CarRentalDbContext>().Set<Invoice>()
                    .AsNoTracking()
                    .Include(i => i.Lines)
                    .FirstOrDefaultAsync(i => i.BookingId == bookingId && i.Kind == kind, Ct);
            },
            $"the {kind} invoice of {bookingId}",
            Ct);

    /// <summary>The facts of the month as the contracts of the other modules give them to the journals.</summary>
    private async Task<MonthFacts> FactsAsync(Guid agencyId, UserContextSnapshot actor)
    {
        await using var scope = month.Host.CreateScope(actor);
        var services = scope.ServiceProvider;
        var (from, to) = (March.FirstDay, March.LastDay);
        var sales = await services.GetRequiredService<IBillingQueries>().GetSalesDocumentsAsync(agencyId, from, to, Ct);
        var payments = await services.GetRequiredService<IPaymentsAccountingQueries>().GetSettledPaymentsAsync(agencyId, from, to, Ct);
        var deposits = await services.GetRequiredService<IPaymentsAccountingQueries>().GetDepositMovementsAsync(agencyId, from, to, Ct);
        var expenses = await services.GetRequiredService<ICashAccountingQueries>().GetExpensesAsync(agencyId, from, to, Ct);
        var sessions = await services.GetRequiredService<ICashQueries>().GetSessionTotalsAsync(from, to, agencyId, Ct);
        var payouts = await services.GetRequiredService<IChannelPayouts>().GetRecordedPayoutsAsync(agencyId, from, to, Ct);
        var statement = await services.GetRequiredService<CarRentalDbContext>().Set<MerchantStatement>()
            .AsNoTracking()
            .Where(s => s.AgencyId == agencyId)
            .SelectMany(s => s.Lines)
            .ToListAsync(Ct);
        var invoicedBookings = sales.Where(s => s.BookingId is not null).Select(s => s.BookingId!.Value).ToHashSet();
        var advance = payments
            .Where(p => !invoicedBookings.Contains(p.BookingId))
            .Select(p => p.Direction == PaymentDirectionInfo.Incoming ? p.Amount : p.Amount.Negate())
            .Sum();
        return new MonthFacts(sales, payments, deposits, expenses, sessions, payouts, statement, advance);
    }

    private sealed record MonthFacts(
        IReadOnlyList<SalesDocumentInfo> Sales,
        IReadOnlyList<SettledPaymentInfo> Payments,
        IReadOnlyList<DepositMovementInfo> Deposits,
        IReadOnlyList<ExpenseEntryInfo> Expenses,
        IReadOnlyList<CashSessionTotalsInfo> Sessions,
        IReadOnlyList<ChannelSettlementInfo> Payouts,
        IReadOnlyList<StatementLine> Statement,
        Money Advance);
}
