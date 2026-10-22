using CarRental.Application.Identity.Contracts;
using CarRental.Application.Payments.Contracts;
using CarRental.Application.Referential.Contracts;
using CarRental.Application.Tests.Payments.Fakes;
using CarRental.Domain.Cash;
using CarRental.Domain.Common;
using CarRental.Domain.Payments;
using static CarRental.Application.Tests.Payments.Fakes.FinanceHarness;

namespace CarRental.Application.Tests.Payments;

/// <summary>Counter payments: permissions, cash through the open session of the agent, refunds, pending payments, totals.</summary>
public sealed class CounterPaymentsTests
{
    private readonly FinanceHarness _harness = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_agent_records_a_card_payment_without_cash_session()
    {
        var agent = _harness.User(Roles.Agent);
        var bookingId = Guid.CreateVersion7();

        var id = (await _harness.Payments(agent).RecordPaymentAsync(
            new CounterPaymentRequest(bookingId, _harness.AgencyId, PaymentMethodInfo.CardTerminal, 450m, " TPE 0042 ", "Solde"), cancellationToken: Ct)).Value;

        var payment = _harness.Payment(id);
        payment.Method.ShouldBe(PaymentMethod.CardTerminal);
        payment.Status.ShouldBe(PaymentStatus.Captured);
        payment.Amount.ShouldBe(Tnd(450m));
        payment.ExternalReference.ShouldBe("TPE 0042");
        payment.CashSessionId.ShouldBeNull();
        payment.RecordedByUserId.ShouldBe(agent.UserId);
        payment.RecordedAtUtc.ShouldBe(Start);
        payment.DomainEvents.OfType<PaymentRecorded>().ShouldHaveSingleItem().BookingId.ShouldBe(bookingId);
        _harness.UnitOfWork.Executions.ShouldBe(1);
    }

    [Fact]
    public async Task A_cash_payment_goes_into_the_open_cash_session_of_the_agent_in_the_same_unit_of_work()
    {
        var agent = _harness.User(Roles.Agent);
        var sessionId = await _harness.OpenSessionAsync(agent, openingFloat: 100m, cancellationToken: Ct);
        var bookingId = Guid.CreateVersion7();

        var id = (await _harness.Payments(agent).RecordPaymentAsync(CashPayment(bookingId, _harness.AgencyId, 250m), cancellationToken: Ct)).Value;

        _harness.Payment(id).CashSessionId.ShouldBe(sessionId);
        var session = _harness.Session(sessionId);
        session.ExpectedRevenueCash.ShouldBe(Tnd(350m));
        var movement = session.Movements.ShouldHaveSingleItem();
        movement.Kind.ShouldBe(CashMovementKind.PaymentReceived);
        movement.PaymentId.ShouldBe(id);
        movement.BookingId.ShouldBe(bookingId);
        movement.RecordedByUserId.ShouldBe(agent.UserId);
    }

    [Fact]
    public async Task An_agency_without_the_cash_module_takes_no_cash_but_the_other_means_of_payment()
    {
        var agent = _harness.User(Roles.Agent);
        await _harness.OpenSessionAsync(agent, openingFloat: 100m, cancellationToken: Ct);
        _harness.Referential.Modules[_harness.AgencyId] = AgencyModuleCatalog.Defaults & ~AgencyModules.Cash;
        var bookingId = Guid.CreateVersion7();

        (await _harness.Payments(agent).RecordPaymentAsync(CashPayment(bookingId, _harness.AgencyId, 250m), cancellationToken: Ct)).Error.ShouldBe(CashErrors.ModuleOff);
        (await _harness.Deposits(agent).TakeAsync(new DepositRequest(bookingId, _harness.AgencyId, DepositModeInfo.Cash, 300m), Ct)).Error.ShouldBe(CashErrors.ModuleOff);
        var card = (await _harness.Payments(agent).RecordPaymentAsync(
            new CounterPaymentRequest(bookingId, _harness.AgencyId, PaymentMethodInfo.CardTerminal, 250m, "TPE 1"), cancellationToken: Ct)).Value;
        (await _harness.Payments(agent).RecordRefundAsync(new CounterRefundRequest(card, PaymentMethodInfo.Cash, 50m, "Trop-perçu"), Ct)).Error.ShouldBe(CashErrors.ModuleOff);
        _harness.Db.Rows<Payment>().ShouldHaveSingleItem().Id.ShouldBe(card);
    }

    [Fact]
    public async Task Cash_cannot_be_recorded_without_an_open_session()
    {
        var agent = _harness.User(Roles.Agent);

        var result = await _harness.Payments(agent).RecordPaymentAsync(CashPayment(Guid.CreateVersion7(), _harness.AgencyId, 50m), cancellationToken: Ct);

        result.Error.ShouldBe(CashErrors.NoOpenSession);
        _harness.Db.Rows<Payment>().ShouldBeEmpty();
    }

    [Fact]
    public async Task Cash_goes_through_ones_own_session_never_the_session_of_another_agent()
    {
        var owner = _harness.User(Roles.Agent);
        await _harness.OpenSessionAsync(owner, cancellationToken: Ct);
        var other = _harness.User(Roles.Agent);

        var result = await _harness.Payments(other).RecordPaymentAsync(CashPayment(Guid.CreateVersion7(), _harness.AgencyId, 50m), cancellationToken: Ct);

        result.Error.ShouldBe(CashErrors.NoOpenSession);
    }

    [Fact]
    public async Task Cash_received_for_another_agency_than_the_open_session_is_refused()
    {
        var otherAgency = Guid.CreateVersion7();
        _harness.Referential.Agencies.Add(new AgencyInfo(otherAgency, "Agence Djerba", "Djerba", null, null, IsActive: true));
        var agent = _harness.User(Roles.Agent);
        await _harness.OpenSessionAsync(agent, cancellationToken: Ct);

        var result = await _harness.Payments(agent).RecordPaymentAsync(CashPayment(Guid.CreateVersion7(), otherAgency, 50m), cancellationToken: Ct);

        result.Error.ShouldBe(CashErrors.SessionAgencyMismatch);
    }

    [Fact]
    public async Task Cash_needs_the_cash_permission_of_the_acting_user()
    {
        var user = FinanceHarness.UserWith(Permissions.PaymentsManage, Permissions.PaymentsRead);

        var result = await _harness.Payments(user).RecordPaymentAsync(CashPayment(Guid.CreateVersion7(), _harness.AgencyId, 50m), cancellationToken: Ct);

        result.Error.ShouldBe(CashErrors.Forbidden);
    }

    [Theory]
    [InlineData(Roles.Agent, true)]
    [InlineData(Roles.Accountant, true)]
    [InlineData(Roles.Manager, true)]
    [InlineData(Roles.Preparer, false)]
    public async Task Recording_a_payment_follows_the_rights_matrix(string role, bool allowed)
    {
        var request = new CounterPaymentRequest(Guid.CreateVersion7(), _harness.AgencyId, PaymentMethodInfo.CardTerminal, 10m);

        var result = await _harness.Payments(_harness.User(role)).RecordPaymentAsync(request, cancellationToken: Ct);

        if (allowed)
        {
            result.IsSuccess.ShouldBeTrue();
        }
        else
        {
            result.Error.ShouldBe(PaymentsErrors.Forbidden);
            _harness.UnitOfWork.Executions.ShouldBe(0);
        }
    }

    [Theory]
    [InlineData(10.0001)]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(1000000.001)]
    public async Task Amounts_are_positive_millimes_never_rounded(decimal amount)
    {
        var request = new CounterPaymentRequest(Guid.CreateVersion7(), _harness.AgencyId, PaymentMethodInfo.CardTerminal, amount);

        (await _harness.Payments(_harness.User(Roles.Agent)).RecordPaymentAsync(request, cancellationToken: Ct)).Error.ShouldBe(PaymentsErrors.AmountInvalid);
    }

    [Fact]
    public async Task An_unknown_or_inactive_agency_is_refused()
    {
        var inactive = Guid.CreateVersion7();
        _harness.Referential.Agencies.Add(new AgencyInfo(inactive, "Ancienne agence", "Sousse", null, null, IsActive: false));
        var payments = _harness.Payments(_harness.User(Roles.Agent));

        (await payments.RecordPaymentAsync(new CounterPaymentRequest(Guid.CreateVersion7(), Guid.CreateVersion7(), PaymentMethodInfo.CardTerminal, 10m), cancellationToken: Ct))
            .Error.ShouldBe(PaymentsErrors.AgencyNotFound);
        (await payments.RecordPaymentAsync(new CounterPaymentRequest(Guid.CreateVersion7(), inactive, PaymentMethodInfo.CardTerminal, 10m), cancellationToken: Ct))
            .Error.ShouldBe(PaymentsErrors.AgencyNotFound);
    }

    [Fact]
    public async Task Clic_to_pay_is_never_recorded_at_the_counter()
    {
        var request = new CounterPaymentRequest(Guid.CreateVersion7(), _harness.AgencyId, PaymentMethodInfo.ClicToPay, 10m, "ORDER-1");

        (await _harness.Payments(_harness.User(Roles.Manager)).RecordPaymentAsync(request, cancellationToken: Ct)).Error.ShouldBe(PaymentsErrors.MethodNotCounter);
    }

    [Fact]
    public async Task A_pending_transfer_counts_as_paid_once_confirmed()
    {
        var agent = _harness.User(Roles.Agent);
        var payments = _harness.Payments(agent);
        var bookingId = Guid.CreateVersion7();
        var id = (await payments.RecordPaymentAsync(new CounterPaymentRequest(bookingId, _harness.AgencyId, PaymentMethodInfo.BankTransfer, 600m, "VIR-77", AwaitingConfirmation: true), cancellationToken: Ct)).Value;

        var before = (await payments.GetBookingPaymentsAsync(bookingId, cancellationToken: Ct)).Value.Totals;
        _harness.Time.Advance(TimeSpan.FromDays(1));
        (await payments.ConfirmPendingAsync(id, "VIR-77 / BIAT", cancellationToken: Ct)).IsSuccess.ShouldBeTrue();
        var after = (await payments.GetBookingPaymentsAsync(bookingId, cancellationToken: Ct)).Value.Totals;

        before.ShouldBe(new PaymentTotals(Money.Zero, Money.Zero, Money.Zero, Tnd(600m), Money.Zero));
        after.ShouldBe(new PaymentTotals(Tnd(600m), Money.Zero, Tnd(600m), Money.Zero, Money.Zero));
        _harness.Payment(id).CapturedAtUtc.ShouldBe(Start.AddDays(1));
        _harness.Payment(id).ExternalReference.ShouldBe("VIR-77 / BIAT");
    }

    [Fact]
    public async Task A_pending_cheque_can_be_cancelled_with_a_reason()
    {
        var payments = _harness.Payments(_harness.User(Roles.Accountant));
        var id = (await payments.RecordPaymentAsync(new CounterPaymentRequest(Guid.CreateVersion7(), _harness.AgencyId, PaymentMethodInfo.Cheque, 300m, "CHQ 1234567", AwaitingConfirmation: true), cancellationToken: Ct)).Value;

        (await payments.CancelPendingAsync(id, " ", cancellationToken: Ct)).Error.ShouldBe(PaymentsErrors.ReasonRequired);
        (await payments.CancelPendingAsync(id, "Chèque rendu au client", cancellationToken: Ct)).IsSuccess.ShouldBeTrue();
        (await payments.ConfirmPendingAsync(id, cancellationToken: Ct)).Error.ShouldBe(PaymentsErrors.PaymentNotPending);
        (await payments.ConfirmPendingAsync(Guid.CreateVersion7(), cancellationToken: Ct)).Error.ShouldBe(PaymentsErrors.PaymentNotFound);

        _harness.Payment(id).Status.ShouldBe(PaymentStatus.Cancelled);
        (await _harness.Payments(_harness.User(Roles.Preparer)).CancelPendingAsync(id, "Raison", cancellationToken: Ct)).Error.ShouldBe(PaymentsErrors.Forbidden);
    }

    [Fact]
    public async Task A_cash_refund_is_paid_from_the_drawer_of_the_agent()
    {
        var agent = _harness.User(Roles.Agent);
        var sessionId = await _harness.OpenSessionAsync(agent, openingFloat: 0m, cancellationToken: Ct);
        var payments = _harness.Payments(agent);
        var bookingId = Guid.CreateVersion7();
        var paymentId = (await payments.RecordPaymentAsync(CashPayment(bookingId, _harness.AgencyId, 200m), cancellationToken: Ct)).Value;

        var refundId = (await payments.RecordRefundAsync(new CounterRefundRequest(paymentId, PaymentMethodInfo.Cash, 80m, "Annulation d'une option"), cancellationToken: Ct)).Value;

        var refund = _harness.Payment(refundId);
        refund.Direction.ShouldBe(PaymentDirection.Refund);
        refund.OriginalPaymentId.ShouldBe(paymentId);
        refund.CashSessionId.ShouldBe(sessionId);
        _harness.Payment(paymentId).RefundedAmount.ShouldBe(Tnd(80m));
        var session = _harness.Session(sessionId);
        session.ExpectedRevenueCash.ShouldBe(Tnd(120m));
        session.Movements[^1].Kind.ShouldBe(CashMovementKind.RefundPaid);
        session.Movements[^1].PaymentId.ShouldBe(refundId);
        (await payments.GetBookingPaymentsAsync(bookingId, cancellationToken: Ct)).Value.Totals.ShouldBe(new PaymentTotals(Tnd(200m), Tnd(80m), Tnd(120m), Money.Zero, Money.Zero));
    }

    [Fact]
    public async Task A_cash_refund_larger_than_the_drawer_is_refused()
    {
        var agent = _harness.User(Roles.Agent);
        await _harness.OpenSessionAsync(agent, openingFloat: 10m, cancellationToken: Ct);
        var payments = _harness.Payments(agent);
        var paymentId = (await payments.RecordPaymentAsync(new CounterPaymentRequest(Guid.CreateVersion7(), _harness.AgencyId, PaymentMethodInfo.CardTerminal, 500m), cancellationToken: Ct)).Value;

        var result = await payments.RecordRefundAsync(new CounterRefundRequest(paymentId, PaymentMethodInfo.Cash, 50m, "Trop-perçu"), cancellationToken: Ct);

        result.Error.ShouldBe(CashErrors.InsufficientCash);
        _harness.Db.Rows<Payment>().ShouldHaveSingleItem("no refund is stored (the transaction is rolled back on SQL Server)");
    }

    [Fact]
    public async Task Refunds_never_exceed_what_was_captured()
    {
        var payments = _harness.Payments(_harness.User(Roles.Manager));
        var paymentId = (await payments.RecordPaymentAsync(new CounterPaymentRequest(Guid.CreateVersion7(), _harness.AgencyId, PaymentMethodInfo.CardTerminal, 100m), cancellationToken: Ct)).Value;

        (await payments.RecordRefundAsync(new CounterRefundRequest(paymentId, PaymentMethodInfo.CardTerminal, 60m, "Geste commercial"), cancellationToken: Ct)).IsSuccess.ShouldBeTrue();
        (await payments.RecordRefundAsync(new CounterRefundRequest(paymentId, PaymentMethodInfo.CardTerminal, 40.001m, "Encore"), cancellationToken: Ct)).Error.ShouldBe(PaymentsErrors.RefundExceedsCaptured);
        (await payments.RecordRefundAsync(new CounterRefundRequest(paymentId, PaymentMethodInfo.CardTerminal, 40m, "Solde de l'annulation"), cancellationToken: Ct)).IsSuccess.ShouldBeTrue();

        _harness.Payment(paymentId).Status.ShouldBe(PaymentStatus.Refunded);
        (await payments.RecordRefundAsync(new CounterRefundRequest(Guid.CreateVersion7(), PaymentMethodInfo.CardTerminal, 1m, "Inconnu"), cancellationToken: Ct)).Error.ShouldBe(PaymentsErrors.PaymentNotFound);
    }

    [Fact]
    public async Task The_payments_of_a_booking_are_listed_oldest_first_with_their_totals()
    {
        var payments = _harness.Payments(_harness.User(Roles.Agent));
        var bookingId = Guid.CreateVersion7();
        var first = (await payments.RecordPaymentAsync(new CounterPaymentRequest(bookingId, _harness.AgencyId, PaymentMethodInfo.CardTerminal, 300m), cancellationToken: Ct)).Value;
        _harness.Time.Advance(TimeSpan.FromHours(1));
        await payments.RecordPaymentAsync(new CounterPaymentRequest(bookingId, _harness.AgencyId, PaymentMethodInfo.Cheque, 100m, "CHQ 99", AwaitingConfirmation: true), cancellationToken: Ct);
        _harness.Time.Advance(TimeSpan.FromHours(1));
        await payments.RecordRefundAsync(new CounterRefundRequest(first, PaymentMethodInfo.CardTerminal, 50m, "Option retirée"), cancellationToken: Ct);
        await payments.RecordPaymentAsync(new CounterPaymentRequest(Guid.CreateVersion7(), _harness.AgencyId, PaymentMethodInfo.CardTerminal, 999m), cancellationToken: Ct);

        var list = (await payments.GetBookingPaymentsAsync(bookingId, cancellationToken: Ct)).Value;

        list.BookingId.ShouldBe(bookingId);
        list.Payments.Select(p => (p.Direction, p.Method, p.Status, p.Amount.Amount)).ShouldBe(
        [
            (PaymentDirectionInfo.Incoming, PaymentMethodInfo.CardTerminal, PaymentStatusInfo.Captured, 300m),
            (PaymentDirectionInfo.Incoming, PaymentMethodInfo.Cheque, PaymentStatusInfo.Pending, 100m),
            (PaymentDirectionInfo.Refund, PaymentMethodInfo.CardTerminal, PaymentStatusInfo.Captured, 50m),
        ]);
        list.Payments[0].RefundableAmount.ShouldBe(Tnd(250m));
        list.Payments[2].OriginalPaymentId.ShouldBe(first);
        list.Payments[2].Notes.ShouldBe("Option retirée");
        list.Totals.ShouldBe(new PaymentTotals(Tnd(300m), Tnd(50m), Tnd(250m), Tnd(100m), Money.Zero));
        (await _harness.Payments(_harness.User(Roles.Preparer)).GetBookingPaymentsAsync(bookingId, cancellationToken: Ct)).Error.ShouldBe(PaymentsErrors.Forbidden);
        (await _harness.Payments(_harness.User(Roles.Agent)).GetBookingPaymentsAsync(Guid.CreateVersion7(), cancellationToken: Ct)).Value.Totals.ShouldBe(PaymentTotals.Zero);
    }
}
