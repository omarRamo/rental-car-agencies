using CarRental.Domain.Common;
using CarRental.Domain.Payments;
using static CarRental.Domain.Tests.Payments.PaymentTestData;

namespace CarRental.Domain.Tests.Payments;

/// <summary>Counter payments: recording, statuses and their transitions, references, cash sessions.</summary>
public sealed class PaymentTests
{
    [Fact]
    public void A_cash_payment_is_captured_at_once_in_the_cash_session_of_the_agent()
    {
        var details = Counter(PaymentMethod.Cash, 250m) with { Notes = "  Solde à la remise des clés " };

        var payment = Payment.RecordCounter(details, Agent, Now).Value;

        payment.Direction.ShouldBe(PaymentDirection.Incoming);
        payment.Method.ShouldBe(PaymentMethod.Cash);
        payment.Status.ShouldBe(PaymentStatus.Captured);
        payment.Amount.ShouldBe(Tnd(250m));
        payment.CapturedAmount.ShouldBe(Tnd(250m));
        payment.CapturedAtUtc.ShouldBe(Now);
        payment.RefundedAmount.ShouldBe(Money.Zero);
        payment.RefundableAmount.ShouldBe(Tnd(250m));
        payment.IsSettled.ShouldBeTrue();
        payment.CashSessionId.ShouldBe(SessionId);
        payment.AgencyId.ShouldBe(AgencyId);
        payment.BookingId.ShouldBe(details.BookingId);
        payment.Notes.ShouldBe("Solde à la remise des clés");
        payment.RecordedAtUtc.ShouldBe(Now);
        payment.RecordedByUserId.ShouldBe(Agent.UserId);
        payment.RecordedByName.ShouldBe("agent@demo.local");
        payment.DomainEvents.ShouldHaveSingleItem().ShouldBe(new PaymentRecorded(
            payment.Id, details.BookingId, AgencyId, PaymentDirection.Incoming, PaymentMethod.Cash, PaymentStatus.Captured));
    }

    [Theory]
    [InlineData(PaymentMethod.CardTerminal)]
    [InlineData(PaymentMethod.Cheque)]
    [InlineData(PaymentMethod.BankTransfer)]
    [InlineData(PaymentMethod.ChannelPrepaid)]
    public void Every_counter_means_is_captured_at_once_without_cash_session(PaymentMethod method)
    {
        var payment = Payment.RecordCounter(Counter(method, 99.5m), Agent, Now).Value;

        payment.Status.ShouldBe(PaymentStatus.Captured);
        payment.CapturedAmount.ShouldBe(Tnd(99.5m));
        payment.CashSessionId.ShouldBeNull();
    }

    [Theory]
    [InlineData(PaymentMethod.BankTransfer)]
    [InlineData(PaymentMethod.Cheque)]
    public void A_bank_transfer_or_a_cheque_can_await_its_confirmation(PaymentMethod method)
    {
        var payment = Payment.RecordCounter(Counter(method, 400m, awaiting: true), Agent, Now).Value;

        payment.Status.ShouldBe(PaymentStatus.Pending);
        payment.CapturedAmount.ShouldBe(Money.Zero);
        payment.CapturedAtUtc.ShouldBeNull();
        payment.IsSettled.ShouldBeFalse();
        payment.RefundableAmount.ShouldBe(Money.Zero);
        payment.DomainEvents.OfType<PaymentRecorded>().Single().Status.ShouldBe(PaymentStatus.Pending);
    }

    [Theory]
    [InlineData(PaymentMethod.Cash)]
    [InlineData(PaymentMethod.CardTerminal)]
    [InlineData(PaymentMethod.ChannelPrepaid)]
    public void Only_a_transfer_or_a_cheque_can_await_confirmation(PaymentMethod method)
    {
        Payment.RecordCounter(Counter(method, 10m, awaiting: true), Agent, Now).Error.ShouldBe(PaymentsErrors.ConfirmationNotApplicable);
    }

    [Fact]
    public void Counter_payments_are_validated()
    {
        var valid = Counter(PaymentMethod.CardTerminal, 10m);

        Payment.RecordCounter(valid with { AgencyId = Guid.Empty }, Agent, Now).Error.ShouldBe(PaymentsErrors.AgencyRequired);
        Payment.RecordCounter(valid with { BookingId = Guid.Empty }, Agent, Now).Error.ShouldBe(PaymentsErrors.BookingRequired);
        Payment.RecordCounter(valid with { Method = (PaymentMethod)42 }, Agent, Now).Error.ShouldBe(PaymentsErrors.MethodInvalid);
        Payment.RecordCounter(valid with { Method = PaymentMethod.ClicToPay }, Agent, Now).Error.ShouldBe(PaymentsErrors.MethodNotCounter);
        Payment.RecordCounter(valid with { Amount = Money.Zero }, Agent, Now).Error.ShouldBe(PaymentsErrors.AmountInvalid);
        Payment.RecordCounter(valid with { Amount = Tnd(-5m) }, Agent, Now).Error.ShouldBe(PaymentsErrors.AmountInvalid);
        Payment.RecordCounter(valid with { Amount = Tnd(1_000_000.001m) }, Agent, Now).Error.ShouldBe(PaymentsErrors.AmountInvalid);
        Payment.RecordCounter(valid with { Amount = Money.Of(10m, Currency.Eur) }, Agent, Now).Error.ShouldBe(PaymentsErrors.AmountInvalid);
        Payment.RecordCounter(valid with { ExternalReference = new string('x', Payment.ReferenceMaxLength + 1) }, Agent, Now)
            .Error.ShouldBe(PaymentsErrors.ReferenceTooLong);
        Payment.RecordCounter(valid with { Notes = new string('x', Payment.NotesMaxLength + 1) }, Agent, Now)
            .Error.ShouldBe(PaymentsErrors.NotesTooLong);
        Payment.RecordCounter(valid with { Amount = Tnd(1_000_000m) }, Agent, Now).IsSuccess.ShouldBeTrue();
    }

    [Theory]
    [InlineData(PaymentMethod.Cheque)]
    [InlineData(PaymentMethod.BankTransfer)]
    [InlineData(PaymentMethod.ChannelPrepaid)]
    public void Cheques_transfers_and_channel_prepayments_need_their_reference(PaymentMethod method)
    {
        var details = Counter(method, 80m) with { ExternalReference = "   " };

        Payment.RecordCounter(details, Agent, Now).Error.ShouldBe(PaymentsErrors.ReferenceRequired);
        Payment.RecordCounter(details with { ExternalReference = "  CHQ 0012345 " }, Agent, Now).Value.ExternalReference.ShouldBe("CHQ 0012345");
    }

    [Fact]
    public void The_reference_of_cash_and_card_terminal_payments_is_optional()
    {
        Payment.RecordCounter(Counter(PaymentMethod.Cash, 5m), Agent, Now).Value.ExternalReference.ShouldBeNull();
        Payment.RecordCounter(Counter(PaymentMethod.CardTerminal, 5m) with { ExternalReference = "TPE 004512" }, Agent, Now)
            .Value.ExternalReference.ShouldBe("TPE 004512");
    }

    [Fact]
    public void Cash_needs_the_cash_session_and_other_means_never_go_through_one()
    {
        Payment.RecordCounter(Counter(PaymentMethod.Cash, 5m) with { CashSessionId = null }, Agent, Now).Error.ShouldBe(PaymentsErrors.CashSessionRequired);
        Payment.RecordCounter(Counter(PaymentMethod.Cash, 5m) with { CashSessionId = Guid.Empty }, Agent, Now).Error.ShouldBe(PaymentsErrors.CashSessionRequired);

        Should.Throw<ArgumentException>(() => Payment.RecordCounter(Counter(PaymentMethod.CardTerminal, 5m) with { CashSessionId = SessionId }, Agent, Now));
    }

    [Fact]
    public void Confirming_a_pending_payment_captures_it_and_can_complete_its_reference()
    {
        var payment = PendingTransfer(400m);
        payment.DequeueDomainEvents();
        var confirmedAt = Now.AddDays(2);

        payment.Confirm("  VIR-2027-0042 / BIAT ", Manager, confirmedAt).IsSuccess.ShouldBeTrue();

        payment.Status.ShouldBe(PaymentStatus.Captured);
        payment.CapturedAmount.ShouldBe(Tnd(400m));
        payment.CapturedAtUtc.ShouldBe(confirmedAt);
        payment.ExternalReference.ShouldBe("VIR-2027-0042 / BIAT");
        payment.StatusChangedAtUtc.ShouldBe(confirmedAt);
        payment.StatusChangedByUserId.ShouldBe(Manager.UserId);
        payment.StatusChangedByName.ShouldBe("gerant@demo.local");
        payment.RecordedByUserId.ShouldBe(Agent.UserId);
        payment.DomainEvents.ShouldHaveSingleItem().ShouldBe(new PaymentStatusChanged(
            payment.Id, payment.BookingId, PaymentDirection.Incoming, PaymentStatus.Pending, PaymentStatus.Captured));
    }

    [Fact]
    public void Confirming_keeps_the_reference_entered_at_the_recording()
    {
        var payment = PendingTransfer(400m);

        payment.Confirm(null, Agent, Now).IsSuccess.ShouldBeTrue();

        payment.ExternalReference.ShouldBe("VIR-2027-0042");
    }

    [Fact]
    public void Cancelling_a_pending_payment_needs_a_reason()
    {
        var payment = PendingTransfer(400m);
        payment.DequeueDomainEvents();

        payment.Cancel(null, Agent, Now).Error.ShouldBe(PaymentsErrors.ReasonRequired);
        payment.Cancel("  ab ", Agent, Now).Error.ShouldBe(PaymentsErrors.ReasonRequired);
        payment.Cancel(new string('x', Payment.ReasonMaxLength + 1), Agent, Now).Error.ShouldBe(PaymentsErrors.ReasonTooLong);
        payment.Status.ShouldBe(PaymentStatus.Pending);

        payment.Cancel("  Virement jamais reçu ", Agent, Now.AddDays(3)).IsSuccess.ShouldBeTrue();

        payment.Status.ShouldBe(PaymentStatus.Cancelled);
        payment.StatusReason.ShouldBe("Virement jamais reçu");
        payment.StatusChangedAtUtc.ShouldBe(Now.AddDays(3));
        payment.IsSettled.ShouldBeFalse();
        payment.DomainEvents.ShouldHaveSingleItem().ShouldBe(new PaymentStatusChanged(
            payment.Id, payment.BookingId, PaymentDirection.Incoming, PaymentStatus.Pending, PaymentStatus.Cancelled));
    }

    [Fact]
    public void Only_a_pending_payment_can_be_confirmed_or_cancelled()
    {
        var captured = CapturedCash(100m);
        captured.Confirm(null, Agent, Now).Error.ShouldBe(PaymentsErrors.PaymentNotPending);
        captured.Cancel("Erreur de saisie", Agent, Now).Error.ShouldBe(PaymentsErrors.PaymentNotPending);

        var cancelled = PendingTransfer(100m);
        cancelled.Cancel("Virement jamais reçu", Agent, Now).IsSuccess.ShouldBeTrue();
        cancelled.Confirm(null, Agent, Now).Error.ShouldBe(PaymentsErrors.PaymentNotPending);
        cancelled.Cancel("Encore", Agent, Now).Error.ShouldBe(PaymentsErrors.PaymentNotPending);
    }

    [Fact]
    public void A_partial_refund_keeps_the_payment_captured()
    {
        var payment = Captured(PaymentMethod.CardTerminal, 100m);
        payment.DequeueDomainEvents();

        var refund = payment.RecordRefund(CardRefund(40m) with { ExternalReference = " TPE-R-77 " }, Manager, Now.AddHours(2)).Value;

        refund.Direction.ShouldBe(PaymentDirection.Refund);
        refund.OriginalPaymentId.ShouldBe(payment.Id);
        refund.Method.ShouldBe(PaymentMethod.CardTerminal);
        refund.Status.ShouldBe(PaymentStatus.Captured);
        refund.Amount.ShouldBe(Tnd(40m));
        refund.CapturedAmount.ShouldBe(Tnd(40m));
        refund.CapturedAtUtc.ShouldBe(Now.AddHours(2));
        refund.Notes.ShouldBe("Annulation client J-5");
        refund.ExternalReference.ShouldBe("TPE-R-77");
        refund.BookingId.ShouldBe(payment.BookingId);
        refund.AgencyId.ShouldBe(payment.AgencyId);
        refund.RecordedByUserId.ShouldBe(Manager.UserId);
        refund.RefundableAmount.ShouldBe(Money.Zero, "a refund is not refundable");
        refund.DomainEvents.ShouldHaveSingleItem().ShouldBe(new PaymentRecorded(
            refund.Id, payment.BookingId, AgencyId, PaymentDirection.Refund, PaymentMethod.CardTerminal, PaymentStatus.Captured));

        payment.Status.ShouldBe(PaymentStatus.Captured);
        payment.RefundedAmount.ShouldBe(Tnd(40m));
        payment.RefundableAmount.ShouldBe(Tnd(60m));
        payment.DomainEvents.ShouldHaveSingleItem().ShouldBe(new PaymentRefundRecorded(payment.Id, refund.Id, payment.BookingId));
    }

    [Fact]
    public void Paying_back_the_whole_captured_amount_marks_the_payment_refunded()
    {
        var payment = Captured(PaymentMethod.CardTerminal, 100m);
        payment.RecordRefund(CardRefund(33.333m), Agent, Now).IsSuccess.ShouldBeTrue();
        payment.RecordRefund(CardRefund(33.333m), Agent, Now).IsSuccess.ShouldBeTrue();
        payment.DequeueDomainEvents();

        payment.RecordRefund(CardRefund(33.334m), Agent, Now).IsSuccess.ShouldBeTrue();

        payment.Status.ShouldBe(PaymentStatus.Refunded);
        payment.RefundedAmount.ShouldBe(Tnd(100m));
        payment.RefundableAmount.ShouldBe(Money.Zero);
        payment.IsSettled.ShouldBeTrue("the money was received, then paid back");
        payment.DomainEvents.OfType<PaymentStatusChanged>().ShouldHaveSingleItem().Status.ShouldBe(PaymentStatus.Refunded);
        payment.RecordRefund(CardRefund(0.001m), Agent, Now).Error.ShouldBe(PaymentsErrors.RefundExceedsCaptured);
    }

    [Fact]
    public void Refunds_never_exceed_what_was_captured()
    {
        var payment = Captured(PaymentMethod.CardTerminal, 100m);

        payment.RecordRefund(CardRefund(100.001m), Agent, Now).Error.ShouldBe(PaymentsErrors.RefundExceedsCaptured);
        payment.RecordRefund(CardRefund(99.999m), Agent, Now).IsSuccess.ShouldBeTrue();
        payment.RecordRefund(CardRefund(0.002m), Agent, Now).Error.ShouldBe(PaymentsErrors.RefundExceedsCaptured);
        payment.RefundedAmount.ShouldBe(Tnd(99.999m));
        payment.RecordRefund(CardRefund(0.001m), Agent, Now).IsSuccess.ShouldBeTrue();
        payment.Status.ShouldBe(PaymentStatus.Refunded);
    }

    [Fact]
    public void Only_a_captured_incoming_payment_can_be_refunded()
    {
        PendingTransfer(100m).RecordRefund(CardRefund(10m), Agent, Now).Error.ShouldBe(PaymentsErrors.RefundNotCaptured);

        var cancelled = PendingTransfer(100m);
        cancelled.Cancel("Virement jamais reçu", Agent, Now);
        cancelled.RecordRefund(CardRefund(10m), Agent, Now).Error.ShouldBe(PaymentsErrors.RefundNotCaptured);

        var refund = Captured(PaymentMethod.CardTerminal, 100m).RecordRefund(CardRefund(10m), Agent, Now).Value;
        refund.RecordRefund(CardRefund(1m), Agent, Now).Error.ShouldBe(PaymentsErrors.RefundNotAllowed);
    }

    [Fact]
    public void Money_is_paid_back_at_the_counter_with_a_reason()
    {
        var payment = Captured(PaymentMethod.CardTerminal, 100m);

        payment.RecordRefund(CardRefund(10m) with { Method = PaymentMethod.ChannelPrepaid }, Agent, Now).Error.ShouldBe(PaymentsErrors.RefundMethodInvalid);
        payment.RecordRefund(CardRefund(10m) with { Method = PaymentMethod.ClicToPay }, Agent, Now).Error.ShouldBe(PaymentsErrors.RefundMethodInvalid);
        payment.RecordRefund(CardRefund(10m) with { Method = (PaymentMethod)42 }, Agent, Now).Error.ShouldBe(PaymentsErrors.RefundMethodInvalid);
        payment.RecordRefund(CardRefund(0m), Agent, Now).Error.ShouldBe(PaymentsErrors.AmountInvalid);
        payment.RecordRefund(CardRefund(10m, reason: " "), Agent, Now).Error.ShouldBe(PaymentsErrors.RefundReasonRequired);
        payment.RecordRefund(CardRefund(10m, reason: new string('x', Payment.ReasonMaxLength + 1)), Agent, Now).Error.ShouldBe(PaymentsErrors.ReasonTooLong);
        payment.RecordRefund(new CounterRefundDetails(PaymentMethod.Cheque, Tnd(10m), "Trop-perçu"), Agent, Now).Error.ShouldBe(PaymentsErrors.ReferenceRequired);
        payment.RecordRefund(new CounterRefundDetails(PaymentMethod.Cash, Tnd(10m), "Trop-perçu"), Agent, Now).Error.ShouldBe(PaymentsErrors.CashSessionRequired);
        payment.RefundedAmount.ShouldBe(Money.Zero, "a rejected refund changes nothing");
        payment.DomainEvents.OfType<PaymentRefundRecorded>().ShouldBeEmpty();
    }

    [Fact]
    public void A_cash_refund_goes_through_the_cash_session_of_the_agent()
    {
        var payment = Captured(PaymentMethod.CardTerminal, 100m);
        var sessionId = Guid.CreateVersion7();

        var refund = payment.RecordRefund(new CounterRefundDetails(PaymentMethod.Cash, Tnd(25m), "Trop-perçu", CashSessionId: sessionId), Agent, Now).Value;

        refund.Method.ShouldBe(PaymentMethod.Cash);
        refund.CashSessionId.ShouldBe(sessionId);
        Should.Throw<ArgumentException>(() => payment.RecordRefund(CardRefund(1m) with { CashSessionId = sessionId }, Agent, Now));
    }

    [Fact]
    public void Amounts_are_kept_to_the_millime()
    {
        var payment = Payment.RecordCounter(Counter(PaymentMethod.CardTerminal, 0m) with { Amount = Tnd(123.4565m) }, Agent, Now).Value;

        payment.Amount.Amount.ShouldBe(123.457m, "rounded half away from zero by Money");
        payment.RecordRefund(CardRefund(0m) with { Amount = Tnd(0.0004m) }, Agent, Now).Error.ShouldBe(PaymentsErrors.AmountInvalid, "rounds to zero");
        payment.RecordRefund(CardRefund(0m) with { Amount = Tnd(0.0005m) }, Agent, Now).Value.Amount.Amount.ShouldBe(0.001m);
        payment.RefundableAmount.Amount.ShouldBe(123.456m);
    }
}
