using CarRental.Application.Common.Paging;
using CarRental.Application.Identity.Contracts;
using CarRental.Application.Payments.Administration;
using CarRental.Application.Payments.Contracts;
using CarRental.Application.Tests.Payments.Fakes;
using CarRental.Domain.Common;
using CarRental.Domain.Payments;
using static CarRental.Application.Tests.Payments.Fakes.FinanceHarness;

namespace CarRental.Application.Tests.Payments;

/// <summary>Read side: booking summary, daily totals by means, open deposit liabilities, back office lists.</summary>
public sealed class PaymentQueriesTests
{
    private readonly FinanceHarness _harness = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private CounterPaymentRequest Card(Guid bookingId, decimal amount) =>
        new(bookingId, _harness.AgencyId, PaymentMethodInfo.CardTerminal, amount);

    [Fact]
    public async Task The_booking_summary_gives_the_net_paid_and_the_open_deposit_liability()
    {
        var agent = _harness.User(Roles.Agent);
        var bookingId = Guid.CreateVersion7();
        var paymentId = (await _harness.Payments(agent).RecordPaymentAsync(Card(bookingId, 681.4m), cancellationToken: Ct)).Value;
        await _harness.Payments(agent).RecordRefundAsync(new CounterRefundRequest(paymentId, PaymentMethodInfo.CardTerminal, 81.4m, "Siège enfant non fourni"), cancellationToken: Ct);
        var held = (await _harness.Deposits(agent).TakeAsync(new DepositRequest(bookingId, _harness.AgencyId, DepositModeInfo.CardImprint, 800m, "AUT-1"), cancellationToken: Ct)).Value;
        var retained = (await _harness.Deposits(agent).TakeAsync(new DepositRequest(bookingId, _harness.AgencyId, DepositModeInfo.Cheque, 300m, "CHQ-9"), cancellationToken: Ct)).Value;
        await _harness.Deposits(agent).RetainAsync(retained, new DepositRetentionRequest(100m, "Nettoyage"), cancellationToken: Ct);

        var summary = await _harness.PaymentsQueries().GetBookingSummaryAsync(bookingId, cancellationToken: Ct);

        summary.Payments.ShouldBe(new PaymentTotals(Tnd(681.4m), Tnd(81.4m), Tnd(600m), Money.Zero, Money.Zero));
        summary.Deposits.ShouldBe(new DepositTotals(Tnd(1100m), Tnd(800m), Tnd(100m), Tnd(200m), 1));
        _harness.Deposit(held).IsHeld.ShouldBeTrue();
        var unknown = Guid.CreateVersion7();
        (await _harness.PaymentsQueries().GetBookingSummaryAsync(unknown, cancellationToken: Ct)).ShouldBe(new BookingPaymentSummary(unknown, PaymentTotals.Zero, DepositTotals.Zero));
    }

    [Fact]
    public async Task Daily_totals_group_settled_money_by_tunis_day_and_means()
    {
        var agent = _harness.User(Roles.Agent);
        var payments = _harness.Payments(agent);
        var bookingId = Guid.CreateVersion7();

        // 28/06 09:00 Tunis.
        var card = (await payments.RecordPaymentAsync(Card(bookingId, 100m), cancellationToken: Ct)).Value;
        await payments.RecordPaymentAsync(new CounterPaymentRequest(bookingId, _harness.AgencyId, PaymentMethodInfo.BankTransfer, 400m, "VIR-1", AwaitingConfirmation: true), cancellationToken: Ct);
        // 28/06 23:30 UTC = 29/06 00:30 Tunis.
        _harness.Time.SetUtcNow(new DateTimeOffset(2027, 6, 28, 23, 30, 0, TimeSpan.Zero));
        await payments.RecordPaymentAsync(Card(bookingId, 50m), cancellationToken: Ct);
        await payments.RecordRefundAsync(new CounterRefundRequest(card, PaymentMethodInfo.CardTerminal, 30m, "Geste commercial"), cancellationToken: Ct);
        await payments.RecordPaymentAsync(new CounterPaymentRequest(bookingId, _harness.AgencyId, PaymentMethodInfo.Cheque, 70m, "CHQ-2"), cancellationToken: Ct);

        var totals = await _harness.PaymentsQueries().GetDailyTotalsByMethodAsync(new DateOnly(2027, 6, 28), new DateOnly(2027, 6, 29), cancellationToken: Ct);

        totals.ShouldBe(
        [
            new DailyPaymentTotal(new DateOnly(2027, 6, 28), PaymentMethodInfo.CardTerminal, Tnd(100m), Money.Zero, 1, 0),
            new DailyPaymentTotal(new DateOnly(2027, 6, 29), PaymentMethodInfo.CardTerminal, Tnd(50m), Tnd(30m), 1, 1),
            new DailyPaymentTotal(new DateOnly(2027, 6, 29), PaymentMethodInfo.Cheque, Tnd(70m), Money.Zero, 1, 0),
        ]);
        totals[1].Net.ShouldBe(Tnd(20m));
        (await _harness.PaymentsQueries().GetDailyTotalsByMethodAsync(new DateOnly(2027, 6, 29), new DateOnly(2027, 6, 29), Guid.CreateVersion7(), cancellationToken: Ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Open_deposit_liabilities_only_count_held_deposits()
    {
        var agent = _harness.User(Roles.Agent);
        await _harness.OpenSessionAsync(agent, cancellationToken: Ct);
        var deposits = _harness.Deposits(agent);
        await deposits.TakeAsync(new DepositRequest(Guid.CreateVersion7(), _harness.AgencyId, DepositModeInfo.CardImprint, 800m, "AUT-1"), cancellationToken: Ct);
        await deposits.TakeAsync(new DepositRequest(Guid.CreateVersion7(), _harness.AgencyId, DepositModeInfo.CardImprint, 600m, "AUT-2"), cancellationToken: Ct);
        await deposits.TakeAsync(new DepositRequest(Guid.CreateVersion7(), _harness.AgencyId, DepositModeInfo.Cash, 500m), cancellationToken: Ct);
        var released = (await deposits.TakeAsync(new DepositRequest(Guid.CreateVersion7(), _harness.AgencyId, DepositModeInfo.Cheque, 300m, "CHQ-1"), cancellationToken: Ct)).Value;
        await deposits.ReleaseAsync(released, cancellationToken: Ct);

        var liabilities = await _harness.PaymentsQueries().GetOpenDepositLiabilitiesAsync(cancellationToken: Ct);

        liabilities.Total.ShouldBe(Tnd(1900m));
        liabilities.Count.ShouldBe(3);
        liabilities.ByMode.ShouldBe(
        [
            new DepositLiabilityByMode(DepositModeInfo.CardImprint, Tnd(1400m), 2),
            new DepositLiabilityByMode(DepositModeInfo.Cash, Tnd(500m), 1),
        ]);
        (await _harness.PaymentsQueries().GetOpenDepositLiabilitiesAsync(Guid.CreateVersion7(), cancellationToken: Ct)).Total.ShouldBe(Money.Zero);
    }

    [Fact]
    public async Task The_payment_list_filters_and_totals_the_whole_filtered_list()
    {
        var agent = _harness.User(Roles.Agent);
        var payments = _harness.Payments(agent);
        var booking = Guid.CreateVersion7();
        await payments.RecordPaymentAsync(Card(booking, 100m), cancellationToken: Ct);
        await payments.RecordPaymentAsync(new CounterPaymentRequest(booking, _harness.AgencyId, PaymentMethodInfo.Cheque, 40m, "CHQ-1", AwaitingConfirmation: true), cancellationToken: Ct);
        _harness.Time.Advance(TimeSpan.FromDays(1));
        await payments.RecordPaymentAsync(Card(Guid.CreateVersion7(), 250m), cancellationToken: Ct);
        var admin = _harness.Admin(_harness.User(Roles.Accountant));

        var all = (await admin.ListPaymentsAsync(new PaymentListFilter(), new PageRequest(1, 2), cancellationToken: Ct)).Value;
        var cards = (await admin.ListPaymentsAsync(new PaymentListFilter(Method: PaymentMethodInfo.CardTerminal), new PageRequest(), cancellationToken: Ct)).Value;
        var firstDay = (await admin.ListPaymentsAsync(new PaymentListFilter(From: new DateOnly(2027, 6, 28), To: new DateOnly(2027, 6, 28)), new PageRequest(), cancellationToken: Ct)).Value;
        var pending = (await admin.ListPaymentsAsync(new PaymentListFilter(Status: PaymentStatusInfo.Pending, BookingId: booking), new PageRequest(), cancellationToken: Ct)).Value;

        all.Page.TotalCount.ShouldBe(3);
        all.Page.Items.Count.ShouldBe(2);
        all.Page.Items[0].Amount.ShouldBe(Tnd(250m), "newest first");
        all.Totals.ShouldBe(new PaymentTotals(Tnd(350m), Money.Zero, Tnd(350m), Tnd(40m), Money.Zero), "totals of the whole list, not of the page");
        cards.Page.TotalCount.ShouldBe(2);
        firstDay.Page.TotalCount.ShouldBe(2);
        pending.Page.Items.ShouldHaveSingleItem().Method.ShouldBe(PaymentMethodInfo.Cheque);
        pending.Totals.Pending.ShouldBe(Tnd(40m));
        (await _harness.Admin(_harness.User(Roles.Preparer)).ListPaymentsAsync(new PaymentListFilter(), new PageRequest(), cancellationToken: Ct)).Error.ShouldBe(PaymentsErrors.Forbidden);
    }

    [Fact]
    public async Task The_deposit_list_filters_by_status_and_mode_with_its_liability()
    {
        var agent = _harness.User(Roles.Agent);
        var deposits = _harness.Deposits(agent);
        await deposits.TakeAsync(new DepositRequest(Guid.CreateVersion7(), _harness.AgencyId, DepositModeInfo.CardImprint, 800m, "AUT-1"), cancellationToken: Ct);
        var retained = (await deposits.TakeAsync(new DepositRequest(Guid.CreateVersion7(), _harness.AgencyId, DepositModeInfo.Cheque, 300m, "CHQ-1"), cancellationToken: Ct)).Value;
        await deposits.RetainAsync(retained, new DepositRetentionRequest(300m, "Sinistre"), cancellationToken: Ct);
        var admin = _harness.Admin(agent);

        var all = (await admin.ListDepositsAsync(new DepositListFilter(), new PageRequest(), cancellationToken: Ct)).Value;
        var held = (await admin.ListDepositsAsync(new DepositListFilter(Status: DepositStatusInfo.Held), new PageRequest(), cancellationToken: Ct)).Value;
        var cheques = (await admin.ListDepositsAsync(new DepositListFilter(Mode: DepositModeInfo.Cheque), new PageRequest(), cancellationToken: Ct)).Value;

        all.Totals.ShouldBe(new DepositTotals(Tnd(1100m), Tnd(800m), Tnd(300m), Money.Zero, 1));
        held.Page.Items.ShouldHaveSingleItem().Mode.ShouldBe(DepositModeInfo.CardImprint);
        cheques.Page.Items.ShouldHaveSingleItem().Status.ShouldBe(DepositStatusInfo.Retained);
        cheques.Page.Items[0].RetentionJustification.ShouldBe("Sinistre");
        _harness.Db.Rows<DepositHold>().Count.ShouldBe(2);
    }
}
