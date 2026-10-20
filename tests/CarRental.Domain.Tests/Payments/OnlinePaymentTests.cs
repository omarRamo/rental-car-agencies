using CarRental.Domain.Common;
using CarRental.Domain.Payments;
using static CarRental.Domain.Tests.Payments.PaymentTestData;

namespace CarRental.Domain.Tests.Payments;

/// <summary>
/// Invariant 7 (Phase 2 hook): a Clic to Pay payment is pre-authorized, captured, cancelled or failed only from a status
/// confirmed by the payment gateway server to server, never from a counter operation.
/// </summary>
public sealed class OnlinePaymentTests
{
    private static readonly AuditActor Gateway = AuditActor.System("system:clictopay");

    [Fact]
    public void An_online_payment_starts_pending()
    {
        var payment = Online(681.4m);

        payment.Method.ShouldBe(PaymentMethod.ClicToPay);
        payment.IsOnline.ShouldBeTrue();
        payment.Status.ShouldBe(PaymentStatus.Pending);
        payment.CashSessionId.ShouldBeNull();
        payment.DomainEvents.ShouldHaveSingleItem().ShouldBeOfType<PaymentRecorded>().Status.ShouldBe(PaymentStatus.Pending);
        Payment.StartOnline(Guid.Empty, Guid.CreateVersion7(), Tnd(10m), Gateway, Now).Error.ShouldBe(PaymentsErrors.AgencyRequired);
        Payment.StartOnline(AgencyId, Guid.Empty, Tnd(10m), Gateway, Now).Error.ShouldBe(PaymentsErrors.BookingRequired);
        Payment.StartOnline(AgencyId, Guid.CreateVersion7(), Money.Zero, Gateway, Now).Error.ShouldBe(PaymentsErrors.AmountInvalid);
    }

    [Fact]
    public void The_status_of_an_online_payment_never_changes_from_a_counter_operation()
    {
        var payment = Online(100m);

        payment.Confirm("ORDER-1", Agent, Now).Error.ShouldBe(PaymentsErrors.OnlineStatusFromGatewayOnly);
        payment.Cancel("Le client a renoncé", Agent, Now).Error.ShouldBe(PaymentsErrors.OnlineStatusFromGatewayOnly);
        payment.Status.ShouldBe(PaymentStatus.Pending);
    }

    [Fact]
    public void A_pre_authorization_is_captured_from_confirmed_gateway_statuses_possibly_for_less()
    {
        var payment = Online(681.4m);
        payment.DequeueDomainEvents();

        payment.ApplyConfirmedGatewayStatus(new ConfirmedGatewayStatus(GatewayPaymentState.PreAuthorized, Tnd(681.4m), " 7f1c-order ", "approved"), Gateway, Now)
            .IsSuccess.ShouldBeTrue();
        payment.Status.ShouldBe(PaymentStatus.PreAuthorized);
        payment.ExternalReference.ShouldBe("7f1c-order");
        payment.StatusReason.ShouldBe("approved");
        payment.IsSettled.ShouldBeFalse();

        var capturedAt = Now.AddHours(1);
        payment.ApplyConfirmedGatewayStatus(new ConfirmedGatewayStatus(GatewayPaymentState.Deposited, Tnd(650m)), Gateway, capturedAt).IsSuccess.ShouldBeTrue();

        payment.Status.ShouldBe(PaymentStatus.Captured);
        payment.Amount.ShouldBe(Tnd(681.4m));
        payment.CapturedAmount.ShouldBe(Tnd(650m));
        payment.CapturedAtUtc.ShouldBe(capturedAt);
        payment.RefundableAmount.ShouldBe(Tnd(650m));
        payment.DomainEvents.OfType<PaymentStatusChanged>().Select(e => e.Status).ShouldBe([PaymentStatus.PreAuthorized, PaymentStatus.Captured]);
    }

    [Fact]
    public void Replaying_the_confirmed_status_is_idempotent()
    {
        var payment = Online(100m);
        var deposited = new ConfirmedGatewayStatus(GatewayPaymentState.Deposited, Tnd(100m));
        payment.ApplyConfirmedGatewayStatus(deposited, Gateway, Now).IsSuccess.ShouldBeTrue();
        payment.DequeueDomainEvents();

        payment.ApplyConfirmedGatewayStatus(deposited, Gateway, Now.AddMinutes(15)).IsSuccess.ShouldBeTrue();
        payment.ApplyConfirmedGatewayStatus(deposited with { Amount = Tnd(90m) }, Gateway, Now).Error.ShouldBe(PaymentsErrors.GatewayAmountInvalid);

        payment.CapturedAtUtc.ShouldBe(Now);
        payment.DomainEvents.ShouldBeEmpty();
    }

    [Fact]
    public void A_one_step_payment_is_captured_or_declined()
    {
        var paid = Online(100m);
        paid.ApplyConfirmedGatewayStatus(new ConfirmedGatewayStatus(GatewayPaymentState.Registered), Gateway, Now).IsSuccess.ShouldBeTrue();
        paid.Status.ShouldBe(PaymentStatus.Pending);
        paid.ApplyConfirmedGatewayStatus(new ConfirmedGatewayStatus(GatewayPaymentState.Deposited, Tnd(100m)), Gateway, Now).IsSuccess.ShouldBeTrue();
        paid.Status.ShouldBe(PaymentStatus.Captured);

        var declined = Online(100m);
        declined.ApplyConfirmedGatewayStatus(new ConfirmedGatewayStatus(GatewayPaymentState.Declined, Detail: "actionCode 116"), Gateway, Now).IsSuccess.ShouldBeTrue();
        declined.Status.ShouldBe(PaymentStatus.Failed);
        declined.StatusReason.ShouldBe("actionCode 116");
    }

    [Fact]
    public void A_reversed_pre_authorization_is_cancelled()
    {
        var payment = Online(100m);
        payment.ApplyConfirmedGatewayStatus(new ConfirmedGatewayStatus(GatewayPaymentState.PreAuthorized, Tnd(100m)), Gateway, Now);

        payment.ApplyConfirmedGatewayStatus(new ConfirmedGatewayStatus(GatewayPaymentState.Reversed), Gateway, Now).IsSuccess.ShouldBeTrue();

        payment.Status.ShouldBe(PaymentStatus.Cancelled);
        payment.ApplyConfirmedGatewayStatus(new ConfirmedGatewayStatus(GatewayPaymentState.Reversed), Gateway, Now).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void Gateway_statuses_follow_the_life_cycle_only()
    {
        var captured = Online(100m);
        captured.ApplyConfirmedGatewayStatus(new ConfirmedGatewayStatus(GatewayPaymentState.Deposited, Tnd(100m)), Gateway, Now);
        captured.ApplyConfirmedGatewayStatus(new ConfirmedGatewayStatus(GatewayPaymentState.Declined), Gateway, Now).Error.ShouldBe(PaymentsErrors.GatewayTransitionInvalid);
        captured.ApplyConfirmedGatewayStatus(new ConfirmedGatewayStatus(GatewayPaymentState.Reversed), Gateway, Now).Error.ShouldBe(PaymentsErrors.GatewayTransitionInvalid);
        captured.ApplyConfirmedGatewayStatus(new ConfirmedGatewayStatus(GatewayPaymentState.PreAuthorized, Tnd(100m)), Gateway, Now).Error.ShouldBe(PaymentsErrors.GatewayTransitionInvalid);
        captured.ApplyConfirmedGatewayStatus(new ConfirmedGatewayStatus(GatewayPaymentState.Registered), Gateway, Now).Error.ShouldBe(PaymentsErrors.GatewayTransitionInvalid);
        captured.ApplyConfirmedGatewayStatus(new ConfirmedGatewayStatus((GatewayPaymentState)99), Gateway, Now).Error.ShouldBe(PaymentsErrors.GatewayTransitionInvalid);
        captured.Status.ShouldBe(PaymentStatus.Captured);

        var preAuthorized = Online(100m);
        preAuthorized.ApplyConfirmedGatewayStatus(new ConfirmedGatewayStatus(GatewayPaymentState.PreAuthorized, Tnd(100m)), Gateway, Now);
        preAuthorized.ApplyConfirmedGatewayStatus(new ConfirmedGatewayStatus(GatewayPaymentState.Declined), Gateway, Now).Error.ShouldBe(PaymentsErrors.GatewayTransitionInvalid);
    }

    [Fact]
    public void Gateway_amounts_must_match_the_payment()
    {
        var payment = Online(100m);

        payment.ApplyConfirmedGatewayStatus(new ConfirmedGatewayStatus(GatewayPaymentState.PreAuthorized, Tnd(90m)), Gateway, Now).Error.ShouldBe(PaymentsErrors.GatewayAmountInvalid);
        payment.ApplyConfirmedGatewayStatus(new ConfirmedGatewayStatus(GatewayPaymentState.PreAuthorized), Gateway, Now).Error.ShouldBe(PaymentsErrors.GatewayAmountInvalid);
        payment.ApplyConfirmedGatewayStatus(new ConfirmedGatewayStatus(GatewayPaymentState.Deposited, Tnd(100.001m)), Gateway, Now).Error.ShouldBe(PaymentsErrors.GatewayAmountInvalid);
        payment.ApplyConfirmedGatewayStatus(new ConfirmedGatewayStatus(GatewayPaymentState.Deposited), Gateway, Now).Error.ShouldBe(PaymentsErrors.GatewayAmountInvalid);
        payment.ApplyConfirmedGatewayStatus(new ConfirmedGatewayStatus(GatewayPaymentState.Deposited, Money.Zero), Gateway, Now).Error.ShouldBe(PaymentsErrors.GatewayAmountInvalid);
        payment.Status.ShouldBe(PaymentStatus.Pending);
    }

    [Fact]
    public void A_gateway_status_only_applies_to_an_online_payment()
    {
        CapturedCash(10m).ApplyConfirmedGatewayStatus(new ConfirmedGatewayStatus(GatewayPaymentState.Deposited, Tnd(10m)), Gateway, Now)
            .Error.ShouldBe(PaymentsErrors.PaymentNotOnline);
    }

    [Fact]
    public void The_first_gateway_order_id_stays_the_reference()
    {
        var payment = Online(100m);
        payment.ApplyConfirmedGatewayStatus(new ConfirmedGatewayStatus(GatewayPaymentState.Registered, GatewayOrderId: "first"), Gateway, Now);

        payment.ApplyConfirmedGatewayStatus(new ConfirmedGatewayStatus(GatewayPaymentState.Deposited, Tnd(100m), "second"), Gateway, Now).IsSuccess.ShouldBeTrue();
        payment.ApplyConfirmedGatewayStatus(new ConfirmedGatewayStatus(GatewayPaymentState.Deposited, Tnd(100m), new string('x', Payment.ReferenceMaxLength + 1)), Gateway, Now)
            .Error.ShouldBe(PaymentsErrors.ReferenceTooLong);

        payment.ExternalReference.ShouldBe("first");
    }

    [Fact]
    public void A_captured_online_payment_can_be_paid_back_at_the_counter()
    {
        var payment = Online(100m);
        payment.ApplyConfirmedGatewayStatus(new ConfirmedGatewayStatus(GatewayPaymentState.Deposited, Tnd(100m)), Gateway, Now);

        var refund = payment.RecordRefund(CardRefund(30m, "Geste commercial"), Agent, Now).Value;

        refund.Method.ShouldBe(PaymentMethod.CardTerminal);
        payment.RefundedAmount.ShouldBe(Tnd(30m));
    }
}
