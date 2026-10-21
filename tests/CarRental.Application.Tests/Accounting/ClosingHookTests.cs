using CarRental.Application.Cash.Contracts;
using CarRental.Application.Identity.Contracts;
using CarRental.Application.Payments.Contracts;
using CarRental.Application.Tests.Billing.Fakes;
using CarRental.Application.Tests.Payments.Fakes;
using CarRental.Domain.Accounting;
using CarRental.Domain.Billing;
using CarRental.Domain.Cash;
using CarRental.Domain.Common;
using CarRental.Domain.Payments;

namespace CarRental.Application.Tests.Accounting;

/// <summary>
/// The hook of the monthly closing (ADR-0065, <c>IAccountingPeriods.EnsureOpenAsync</c>) in every module that dates a
/// financial fact: once a month is closed, no payment, refund, deposit, capture, cash session, cash movement, invoice or
/// credit note is dated in it — refused before anything is written.
/// </summary>
public sealed class ClosingHookTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task No_counter_payment_refund_or_confirmation_is_dated_in_a_closed_month()
    {
        var harness = new FinanceHarness();
        var agent = harness.User(Roles.Agent);
        var bookingId = Guid.CreateVersion7();
        var payments = harness.Payments(agent);
        var paid = (await payments.RecordPaymentAsync(new CounterPaymentRequest(bookingId, harness.AgencyId, PaymentMethodInfo.CardTerminal, 450m), cancellationToken: Ct)).Value;
        var today = TunisTime.Today(harness.Time);
        harness.Periods.Close(harness.AgencyId, today);

        (await payments.RecordPaymentAsync(new CounterPaymentRequest(bookingId, harness.AgencyId, PaymentMethodInfo.CardTerminal, 50m), cancellationToken: Ct)).Error.ShouldBe(AccountingErrors.PeriodClosed);
        (await payments.RecordRefundAsync(new CounterRefundRequest(paid, PaymentMethodInfo.CardTerminal, 50m, "Geste commercial"), cancellationToken: Ct)).Error.ShouldBe(AccountingErrors.PeriodClosed);

        harness.Db.Rows<Payment>().ShouldHaveSingleItem().Id.ShouldBe(paid);
        harness.Periods.Checks.ShouldAllBe(c => c.AgencyId == harness.AgencyId && c.Day == today);
        harness.Periods.Checks.Count.ShouldBe(3);
    }

    [Fact]
    public async Task No_deposit_is_taken_released_or_retained_in_a_closed_month()
    {
        var harness = new FinanceHarness();
        var deposits = harness.Deposits(harness.User(Roles.Agent));
        var held = (await deposits.TakeAsync(new DepositRequest(Guid.CreateVersion7(), harness.AgencyId, DepositModeInfo.CardImprint, 800m, "AUT-1"), cancellationToken: Ct)).Value;
        harness.Periods.Close(harness.AgencyId, TunisTime.Today(harness.Time));

        (await deposits.TakeAsync(new DepositRequest(Guid.CreateVersion7(), harness.AgencyId, DepositModeInfo.CardImprint, 800m, "AUT-2"), cancellationToken: Ct)).Error.ShouldBe(AccountingErrors.PeriodClosed);
        (await deposits.ReleaseAsync(held, cancellationToken: Ct)).Error.ShouldBe(AccountingErrors.PeriodClosed);

        harness.Deposit(held).IsHeld.ShouldBeTrue();
        harness.Db.Rows<DepositHold>().Count.ShouldBe(1);
    }

    [Fact]
    public async Task No_cash_session_is_opened_and_no_cash_movement_recorded_in_a_closed_month()
    {
        var harness = new FinanceHarness();
        var agent = harness.User(Roles.Agent);
        var sessionId = await harness.OpenSessionAsync(agent, cancellationToken: Ct);
        harness.Periods.Close(harness.AgencyId, harness.Session(sessionId).BusinessDate);

        (await harness.Sessions(agent).AddManualMovementAsync(sessionId, new ManualCashMovementRequest(CashFlowDirectionInfo.Out, 30m, "Dépôt en banque"), cancellationToken: Ct)).Error.ShouldBe(AccountingErrors.PeriodClosed);
        (await harness.Payments(agent).RecordPaymentAsync(FinanceHarness.CashPayment(Guid.CreateVersion7(), harness.AgencyId, 50m), cancellationToken: Ct)).Error.ShouldBe(AccountingErrors.PeriodClosed);
        (await harness.Sessions(harness.User(Roles.Agent)).OpenAsync(new OpenCashSessionRequest(harness.AgencyId, 100m), Ct)).Error.ShouldBe(AccountingErrors.PeriodClosed);

        harness.Session(sessionId).Movements.ShouldBeEmpty();
        harness.Db.Rows<CashSession>().ShouldHaveSingleItem();
    }

    [Fact]
    public async Task No_online_capture_or_refund_is_dated_in_a_closed_month()
    {
        var harness = new OnlinePaymentsHarness();
        var authorized = await harness.PreAuthorizedAsync(300m, Ct);
        var paid = await harness.PaidAsync(200m, Ct);
        harness.Periods.Close(harness.AgencyId, TunisTime.Today(harness.Time));
        var agent = OnlinePaymentsHarness.User(Roles.Agent);

        (await harness.Online(agent).CaptureAsync(authorized.Id, 250m, Ct)).Error.ShouldBe(AccountingErrors.PeriodClosed);
        (await harness.Online(agent).RefundAsync(new OnlineRefundRequest(paid.Id, 50m, "Geste commercial"), Ct)).Error.ShouldBe(AccountingErrors.PeriodClosed);

        authorized.Status.ShouldBe(PaymentStatus.PreAuthorized);
        harness.RefundsOf(paid.Id).ShouldBeEmpty();
        harness.CallsOf(GatewayOperation.Deposit, authorized).ShouldBe(0, "the gateway is never asked");
    }

    [Fact]
    public async Task No_invoice_or_credit_note_is_issued_in_a_closed_month()
    {
        var harness = new BillingHarness();
        var first = harness.ClosedFile();
        var invoice = (await harness.Invoicing.IssueRentalInvoiceAsync(first.BookingId, Ct)).Value;
        var second = harness.ClosedFile();
        harness.Periods.Close(BillingHarness.AgencyId, TunisTime.DateOf(BillingHarness.Now));

        (await harness.Invoicing.IssueRentalInvoiceAsync(second.BookingId, Ct)).Error.ShouldBe(AccountingErrors.PeriodClosed);
        harness.As(Roles.Accountant);
        (await harness.Invoicing.CreateCreditNoteAsync(invoice.Id, new CreditNoteRequest(CreditNoteScope.Total, "Erreur de client"), Ct)).Error.ShouldBe(AccountingErrors.PeriodClosed);

        harness.Db.Rows<Invoice>().ShouldHaveSingleItem().Id.ShouldBe(invoice.Id);
        harness.Db.Rows<CreditNote>().ShouldBeEmpty();
        harness.Periods.Checks.Count.ShouldBe(3);
    }
}
