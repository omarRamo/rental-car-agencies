using CarRental.Domain.Common;
using CarRental.Domain.Payments;
using static CarRental.Domain.Tests.Payments.PaymentTestData;

namespace CarRental.Domain.Tests.Payments;

/// <summary>
/// The Clic to Pay side of an online payment (ADR-0038, ADR-0039): local return paths only (no open redirect), the order
/// of the gateway, the capture and the reversal requested before the call and resolved by a confirmed status, the expiry
/// of an unpaid order and its late payment, and online refunds reserved until the gateway confirms them.
/// </summary>
public sealed class OnlinePaymentGatewayTests
{
    private static readonly AuditActor Gateway = AuditActor.System("system:clictopay");

    private const string OrderId = "8f1c2d34-0000-4000-8000-000000000001";

    private const string FormUrl = "https://test.clictopay.com/payment/merchants/demo/payment.html?mdOrder=8f1c";

    [Theory]
    [InlineData("https://evil.example/phish")]
    [InlineData("//evil.example")]
    [InlineData("/\\evil.example")]
    [InlineData("javascript:alert(1)")]
    [InlineData("fr/reserver")]
    [InlineData("/fr/reserver paiement")]
    [InlineData("/fr/reserver\r\nSet-Cookie:x=1")]
    [InlineData("/fr\\reserver")]
    [InlineData("")]
    public void Return_and_failure_paths_are_local_paths_only(string path)
    {
        Payment.StartOnline(OnlineDetails(10m) with { ReturnPath = path }, Gateway, Now).Error.ShouldBe(PaymentsErrors.OnlinePathInvalid);
        Payment.StartOnline(OnlineDetails(10m) with { FailPath = path }, Gateway, Now).Error.ShouldBe(PaymentsErrors.OnlinePathInvalid);
    }

    [Fact]
    public void A_local_path_may_carry_a_query_and_is_trimmed()
    {
        var payment = Payment.StartOnline(OnlineDetails(10m) with { ReturnPath = " /fr/paiement/{paymentId}?src=lien ", FailPath = "/" }, Gateway, Now).Value;

        payment.GatewayReturnPath.ShouldBe("/fr/paiement/{paymentId}?src=lien");
        payment.GatewayFailPath.ShouldBe("/");
        Payment.StartOnline(OnlineDetails(10m) with { ReturnPath = "/" + new string('a', Payment.LocalPathMaxLength) }, Gateway, Now)
            .Error.ShouldBe(PaymentsErrors.OnlinePathInvalid);
    }

    [Fact]
    public void The_validity_ends_after_the_start_and_within_thirty_days()
    {
        Payment.StartOnline(OnlineDetails(10m) with { ExpiresAtUtc = Now }, Gateway, Now).Error.ShouldBe(PaymentsErrors.OnlineExpiryInvalid);
        Payment.StartOnline(OnlineDetails(10m) with { ExpiresAtUtc = Now.AddDays(31) }, Gateway, Now).Error.ShouldBe(PaymentsErrors.OnlineExpiryInvalid);

        var link = Online(250m, OnlinePaymentFlow.OneStep, TimeSpan.FromHours(24), isLink: true);
        link.IsPaymentLink.ShouldBeTrue();
        link.OnlineExpiresAtUtc.ShouldBe(Now.AddHours(24));
    }

    [Fact]
    public void The_order_registered_by_the_gateway_becomes_the_reference_once()
    {
        var payment = Online(100m);

        payment.RecordGatewayRegistration($" {OrderId} ", FormUrl, Now).IsSuccess.ShouldBeTrue();
        payment.RecordGatewayRegistration(OrderId, FormUrl, Now.AddMinutes(1)).IsSuccess.ShouldBeTrue();

        payment.ExternalReference.ShouldBe(OrderId);
        payment.GatewayFormUrl.ShouldBe(FormUrl);
        payment.GatewayRegisteredAtUtc.ShouldBe(Now);
        payment.Status.ShouldBe(PaymentStatus.Pending);
        payment.RecordGatewayRegistration("another-order", FormUrl, Now).Error.ShouldBe(PaymentsErrors.OnlineGatewayOrderMismatch);
        Online(100m).RecordGatewayRegistration(OrderId, " ", Now).Error.ShouldBe(PaymentsErrors.OnlineFormUrlInvalid);
        Online(100m).RecordGatewayRegistration(OrderId, "https://x/" + new string('a', Payment.GatewayFormUrlMaxLength), Now).Error.ShouldBe(PaymentsErrors.OnlineFormUrlInvalid);
        Online(100m).RecordGatewayRegistration(new string('x', Payment.ReferenceMaxLength + 1), FormUrl, Now).Error.ShouldBe(PaymentsErrors.ReferenceTooLong);
        CapturedCash(10m).RecordGatewayRegistration(OrderId, FormUrl, Now).Error.ShouldBe(PaymentsErrors.PaymentNotOnline);
    }

    [Fact]
    public void A_capture_is_requested_up_to_the_pre_authorized_amount_and_resolved_by_the_confirmed_deposit()
    {
        var payment = OnlineConfirmed(681.4m, GatewayPaymentState.PreAuthorized);

        payment.RequestGatewayCapture(Tnd(681.401m), Now).Error.ShouldBe(PaymentsErrors.OnlineCaptureExceedsAuthorization);
        payment.RequestGatewayCapture(Money.Zero, Now).Error.ShouldBe(PaymentsErrors.OnlineCaptureExceedsAuthorization);
        payment.RequestGatewayCapture(Tnd(650m), Now).IsSuccess.ShouldBeTrue();
        payment.RequestGatewayCapture(Tnd(650m), Now).Error.ShouldBe(PaymentsErrors.OnlineOperationInProgress);
        payment.RequestGatewayReverse(Now).Error.ShouldBe(PaymentsErrors.OnlineOperationInProgress);
        payment.PendingGatewayOperation.ShouldBe(PendingGatewayOperation.Capture);
        payment.PendingGatewayAmount.ShouldBe(Tnd(650m));
        payment.AcknowledgeGatewayOperation(PendingGatewayOperation.Capture).IsSuccess.ShouldBeTrue();
        payment.PendingGatewayAcknowledged.ShouldBeTrue();

        payment.ApplyConfirmedGatewayStatus(new ConfirmedGatewayStatus(GatewayPaymentState.Deposited, Tnd(650m)), Gateway, Now.AddMinutes(1)).IsSuccess.ShouldBeTrue();

        payment.Status.ShouldBe(PaymentStatus.Captured);
        payment.CapturedAmount.ShouldBe(Tnd(650m));
        payment.PendingGatewayOperation.ShouldBeNull();
        payment.PendingGatewayAmount.ShouldBeNull();
        payment.PendingGatewayAcknowledged.ShouldBeFalse();
        payment.GatewayAttention.ShouldBeNull();
    }

    [Fact]
    public void A_deposit_of_another_amount_than_requested_is_recorded_and_flagged()
    {
        var payment = OnlineConfirmed(500m, GatewayPaymentState.PreAuthorized);
        payment.RequestGatewayCapture(Tnd(400m), Now).IsSuccess.ShouldBeTrue();

        payment.ApplyConfirmedGatewayStatus(new ConfirmedGatewayStatus(GatewayPaymentState.Deposited, Tnd(450m)), Gateway, Now).IsSuccess.ShouldBeTrue();

        payment.CapturedAmount.ShouldBe(Tnd(450m), "the gateway is the source of truth");
        payment.GatewayAttention.ShouldBe(GatewayAttention.AmountMismatch);
        payment.GatewayAttentionDetail.ShouldBe("requested 400.000 TND, deposited 450.000 TND");
    }

    [Fact]
    public void Only_a_pre_authorization_is_captured()
    {
        Online(100m, OnlinePaymentFlow.OneStep).RequestGatewayCapture(Tnd(100m), Now).Error.ShouldBe(PaymentsErrors.OnlineCaptureNotApplicable);
        Online(100m).RequestGatewayCapture(Tnd(100m), Now).Error.ShouldBe(PaymentsErrors.OnlineNotPreAuthorized);
        CapturedCash(100m).RequestGatewayCapture(Tnd(100m), Now).Error.ShouldBe(PaymentsErrors.PaymentNotOnline);
    }

    [Fact]
    public void A_reversal_is_requested_once_and_resolved_by_the_confirmed_reversal()
    {
        var payment = OnlineConfirmed(300m, GatewayPaymentState.PreAuthorized);

        payment.RequestGatewayReverse(Now).IsSuccess.ShouldBeTrue();
        payment.RequestGatewayReverse(Now.AddMinutes(1)).IsSuccess.ShouldBeTrue();
        payment.PendingGatewaySinceUtc.ShouldBe(Now, "requesting it again keeps the first request");
        payment.RequestGatewayCapture(Tnd(300m), Now).Error.ShouldBe(PaymentsErrors.OnlineOperationInProgress);

        payment.ApplyConfirmedGatewayStatus(new ConfirmedGatewayStatus(GatewayPaymentState.Reversed, Detail: "status 3"), Gateway, Now).IsSuccess.ShouldBeTrue();

        payment.Status.ShouldBe(PaymentStatus.Cancelled);
        payment.StatusReason.ShouldBe("status 3");
        payment.PendingGatewayOperation.ShouldBeNull();
        payment.ExpiredAtUtc.ShouldBeNull();
        payment.ApplyConfirmedGatewayStatus(new ConfirmedGatewayStatus(GatewayPaymentState.Deposited, Tnd(300m)), Gateway, Now)
            .Error.ShouldBe(PaymentsErrors.GatewayTransitionInvalid, "a reversed order is never reopened");
    }

    [Fact]
    public void A_captured_payment_cannot_be_reversed_and_a_release_asked_before_payment_waits()
    {
        OnlineConfirmed(100m, GatewayPaymentState.Deposited).RequestGatewayReverse(Now).Error.ShouldBe(PaymentsErrors.OnlineAlreadyCaptured);

        var unpaid = Online(100m);
        unpaid.RequestGatewayReverse(Now).IsSuccess.ShouldBeTrue();
        unpaid.ApplyConfirmedGatewayStatus(new ConfirmedGatewayStatus(GatewayPaymentState.PreAuthorized, Tnd(100m)), Gateway, Now).IsSuccess.ShouldBeTrue();

        unpaid.Status.ShouldBe(PaymentStatus.PreAuthorized);
        unpaid.PendingGatewayOperation.ShouldBe(PendingGatewayOperation.Reverse, "the release still has to be sent");
    }

    [Fact]
    public void A_deposit_while_a_reversal_was_requested_is_recorded_and_flagged()
    {
        var payment = OnlineConfirmed(100m, GatewayPaymentState.PreAuthorized);
        payment.RequestGatewayReverse(Now).IsSuccess.ShouldBeTrue();

        payment.ApplyConfirmedGatewayStatus(new ConfirmedGatewayStatus(GatewayPaymentState.Deposited, Tnd(100m)), Gateway, Now).IsSuccess.ShouldBeTrue();

        payment.Status.ShouldBe(PaymentStatus.Captured);
        payment.PendingGatewayOperation.ShouldBeNull();
        payment.GatewayAttention.ShouldBe(GatewayAttention.StatusMismatch);
    }

    [Fact]
    public void An_unpaid_order_expires_only_after_its_validity()
    {
        var payment = Online(120m, OnlinePaymentFlow.OneStep, TimeSpan.FromHours(24), isLink: true);
        payment.DequeueDomainEvents();
        var expired = new ConfirmedGatewayStatus(GatewayPaymentState.Expired, Detail: "status 0 after the validity");

        payment.ApplyConfirmedGatewayStatus(expired, Gateway, Now.AddHours(23)).Error.ShouldBe(PaymentsErrors.OnlineNotExpired);
        payment.ApplyConfirmedGatewayStatus(expired, Gateway, Now.AddHours(24)).IsSuccess.ShouldBeTrue();
        payment.ApplyConfirmedGatewayStatus(expired, Gateway, Now.AddHours(25)).IsSuccess.ShouldBeTrue();
        payment.ApplyConfirmedGatewayStatus(new ConfirmedGatewayStatus(GatewayPaymentState.Registered), Gateway, Now.AddHours(25)).IsSuccess.ShouldBeTrue();
        payment.ApplyConfirmedGatewayStatus(new ConfirmedGatewayStatus(GatewayPaymentState.Declined), Gateway, Now.AddHours(25)).IsSuccess.ShouldBeTrue();

        payment.Status.ShouldBe(PaymentStatus.Cancelled);
        payment.ExpiredAtUtc.ShouldBe(Now.AddHours(24));
        payment.StatusReason.ShouldBe("status 0 after the validity");
        payment.DomainEvents.OfType<OnlinePaymentExpired>().ShouldHaveSingleItem().PaymentId.ShouldBe(payment.Id);
        payment.DomainEvents.OfType<PaymentStatusChanged>().ShouldHaveSingleItem().Status.ShouldBe(PaymentStatus.Cancelled);
        OnlineConfirmed(10m, GatewayPaymentState.PreAuthorized).ApplyConfirmedGatewayStatus(expired, Gateway, Now.AddDays(2))
            .Error.ShouldBe(PaymentsErrors.GatewayTransitionInvalid, "only an unpaid order expires");
    }

    [Fact]
    public void A_late_payment_of_an_expired_order_is_still_recorded_and_flagged()
    {
        var link = Online(120m, OnlinePaymentFlow.OneStep);
        link.ApplyConfirmedGatewayStatus(new ConfirmedGatewayStatus(GatewayPaymentState.Expired), Gateway, Now.AddHours(2)).IsSuccess.ShouldBeTrue();

        link.ApplyConfirmedGatewayStatus(new ConfirmedGatewayStatus(GatewayPaymentState.Deposited, Tnd(120m)), Gateway, Now.AddHours(3)).IsSuccess.ShouldBeTrue();

        link.Status.ShouldBe(PaymentStatus.Captured);
        link.CapturedAmount.ShouldBe(Tnd(120m));
        link.GatewayAttention.ShouldBe(GatewayAttention.LatePayment);

        var attempt = Online(80m);
        attempt.ApplyConfirmedGatewayStatus(new ConfirmedGatewayStatus(GatewayPaymentState.Expired), Gateway, Now.AddHours(2)).IsSuccess.ShouldBeTrue();
        attempt.ApplyConfirmedGatewayStatus(new ConfirmedGatewayStatus(GatewayPaymentState.PreAuthorized, Tnd(80m)), Gateway, Now.AddHours(3)).IsSuccess.ShouldBeTrue();
        attempt.Status.ShouldBe(PaymentStatus.PreAuthorized);
        attempt.GatewayAttention.ShouldBe(GatewayAttention.LatePayment);
    }

    [Fact]
    public void A_pending_operation_is_acknowledged_restarted_or_abandoned_with_a_flag()
    {
        var payment = OnlineConfirmed(200m, GatewayPaymentState.PreAuthorized);
        payment.RequestGatewayCapture(Tnd(200m), Now).IsSuccess.ShouldBeTrue();
        payment.AcknowledgeGatewayOperation(PendingGatewayOperation.Reverse).Error.ShouldBe(PaymentsErrors.GatewayTransitionInvalid);
        payment.AcknowledgeGatewayOperation(PendingGatewayOperation.Capture).IsSuccess.ShouldBeTrue();

        payment.RestartGatewayOperation(Now.AddMinutes(10)).IsSuccess.ShouldBeTrue();
        payment.PendingGatewayAcknowledged.ShouldBeFalse();
        payment.PendingGatewaySinceUtc.ShouldBe(Now.AddMinutes(10));
        payment.PendingGatewayAmount.ShouldBe(Tnd(200m));

        payment.AbandonGatewayOperation(PendingGatewayOperation.Capture, GatewayAttention.CaptureNotConfirmed, "status 1 after deposit", Now).IsSuccess.ShouldBeTrue();
        payment.PendingGatewayOperation.ShouldBeNull();
        payment.GatewayAttention.ShouldBe(GatewayAttention.CaptureNotConfirmed);
        payment.GatewayAttentionAtUtc.ShouldBe(Now);
        payment.RestartGatewayOperation(Now).Error.ShouldBe(PaymentsErrors.GatewayTransitionInvalid);
        payment.Status.ShouldBe(PaymentStatus.PreAuthorized);
    }

    [Fact]
    public void Attention_flags_and_status_codes_are_kept_for_the_back_office()
    {
        var payment = Online(100m);

        payment.RecordGatewayStatusCode(7).IsSuccess.ShouldBeTrue();
        payment.RecordGatewayStatusCode(null).IsSuccess.ShouldBeTrue();
        payment.FlagGatewayAttention(GatewayAttention.UnknownStatus, "status 7 " + new string('x', 300), Now).IsSuccess.ShouldBeTrue();

        payment.GatewayStatusCode.ShouldBe(7);
        payment.GatewayAttention.ShouldBe(GatewayAttention.UnknownStatus);
        payment.GatewayAttentionDetail!.Length.ShouldBe(Payment.GatewayAttentionDetailMaxLength);
        payment.ClearGatewayAttention().IsSuccess.ShouldBeTrue();
        payment.GatewayAttention.ShouldBeNull();
        payment.GatewayAttentionDetail.ShouldBeNull();
        CapturedCash(10m).FlagGatewayAttention(GatewayAttention.UnknownStatus, null, Now).Error.ShouldBe(PaymentsErrors.PaymentNotOnline);
        Should.Throw<ArgumentOutOfRangeException>(() => payment.FlagGatewayAttention((GatewayAttention)99, null, Now));
    }

    [Fact]
    public void An_online_refund_reserves_its_amount_until_the_gateway_confirms_it()
    {
        var payment = OnlineConfirmed(500m, GatewayPaymentState.Deposited);

        var refund = payment.RequestOnlineRefund(Tnd(200m), "Annulation J-5", requiresApproval: false, Manager, Now).Value;

        refund.Direction.ShouldBe(PaymentDirection.Refund);
        refund.Method.ShouldBe(PaymentMethod.ClicToPay);
        refund.Status.ShouldBe(PaymentStatus.Pending);
        refund.OriginalPaymentId.ShouldBe(payment.Id);
        refund.Notes.ShouldBe("Annulation J-5");
        refund.ExternalReference.ShouldBe(OrderId);
        payment.PendingRefundAmount.ShouldBe(Tnd(200m));
        payment.RefundableAmount.ShouldBe(Tnd(300m));
        payment.DomainEvents.OfType<OnlineRefundRequested>().ShouldHaveSingleItem().RequiresApproval.ShouldBeFalse();
        payment.RecordRefund(CardRefund(300.001m), Agent, Now).Error.ShouldBe(PaymentsErrors.RefundExceedsCaptured, "a counter refund respects the reservation");
        payment.RequestOnlineRefund(Tnd(300.001m), "Trop-perçu", false, Manager, Now).Error.ShouldBe(PaymentsErrors.RefundExceedsCaptured);

        refund.MarkOnlineRefundSent(Now).IsSuccess.ShouldBeTrue();
        refund.MarkOnlineRefundSent(Now).Error.ShouldBe(PaymentsErrors.OnlineOperationInProgress);
        payment.ConfirmOnlineRefund(refund, Gateway, Now.AddMinutes(1)).IsSuccess.ShouldBeTrue();
        payment.ConfirmOnlineRefund(refund, Gateway, Now.AddMinutes(2)).IsSuccess.ShouldBeTrue();

        refund.Status.ShouldBe(PaymentStatus.Captured);
        refund.CapturedAmount.ShouldBe(Tnd(200m));
        refund.CapturedAtUtc.ShouldBe(Now.AddMinutes(1));
        refund.PendingGatewayOperation.ShouldBeNull();
        payment.PendingRefundAmount.ShouldBe(Money.Zero);
        payment.RefundedAmount.ShouldBe(Tnd(200m));
        payment.Status.ShouldBe(PaymentStatus.Captured);
        payment.DomainEvents.OfType<PaymentRefundRecorded>().ShouldHaveSingleItem().RefundId.ShouldBe(refund.Id);
    }

    [Fact]
    public void Refunding_the_whole_captured_amount_online_makes_the_payment_refunded()
    {
        var payment = OnlineConfirmed(150m, GatewayPaymentState.Deposited);
        var refund = payment.RequestOnlineRefund(Tnd(150m), "Annulation gratuite", false, Manager, Now).Value;
        refund.MarkOnlineRefundSent(Now).IsSuccess.ShouldBeTrue();

        payment.ConfirmOnlineRefund(refund, Gateway, Now).IsSuccess.ShouldBeTrue();

        payment.Status.ShouldBe(PaymentStatus.Refunded);
        payment.RefundableAmount.ShouldBe(Money.Zero);
        payment.RequestOnlineRefund(Tnd(1m), "Encore", false, Manager, Now).Error.ShouldBe(PaymentsErrors.RefundExceedsCaptured);
    }

    [Fact]
    public void A_refund_above_the_threshold_waits_for_the_approval_of_a_manager()
    {
        var payment = OnlineConfirmed(900m, GatewayPaymentState.Deposited);
        var refund = payment.RequestOnlineRefund(Tnd(800m), "Annulation client", requiresApproval: true, Agent, Now).Value;

        refund.IsAwaitingApproval.ShouldBeTrue();
        refund.MarkOnlineRefundSent(Now).Error.ShouldBe(PaymentsErrors.RefundApprovalRequired);
        refund.ApproveOnlineRefund(Manager, Now.AddHours(1)).IsSuccess.ShouldBeTrue();
        refund.ApproveOnlineRefund(Manager, Now.AddHours(1)).Error.ShouldBe(PaymentsErrors.RefundNotAwaitingApproval);

        refund.IsAwaitingApproval.ShouldBeFalse();
        refund.ApprovedByName.ShouldBe(Manager.UserName);
        refund.ApprovedByUserId.ShouldBe(Manager.UserId);
        refund.ApprovedAtUtc.ShouldBe(Now.AddHours(1));
        refund.MarkOnlineRefundSent(Now.AddHours(1)).IsSuccess.ShouldBeTrue();
        payment.DomainEvents.OfType<OnlineRefundRequested>().ShouldHaveSingleItem().RequiresApproval.ShouldBeTrue();
    }

    [Fact]
    public void A_refused_or_rejected_online_refund_releases_its_reservation()
    {
        var payment = OnlineConfirmed(400m, GatewayPaymentState.Deposited);
        var refused = payment.RequestOnlineRefund(Tnd(100m), "Geste commercial", false, Manager, Now).Value;
        refused.MarkOnlineRefundSent(Now).IsSuccess.ShouldBeTrue();
        var rejected = payment.RequestOnlineRefund(Tnd(250m), "Annulation tardive", true, Agent, Now).Value;
        payment.PendingRefundAmount.ShouldBe(Tnd(350m));

        payment.ReleaseOnlineRefund(refused, PaymentStatus.Failed, "refund.do error 7", Gateway, Now).IsSuccess.ShouldBeTrue();
        payment.ReleaseOnlineRefund(refused, PaymentStatus.Failed, "again", Gateway, Now).IsSuccess.ShouldBeTrue();
        payment.ReleaseOnlineRefund(rejected, PaymentStatus.Cancelled, "Hors politique d'annulation", Manager, Now).IsSuccess.ShouldBeTrue();

        refused.Status.ShouldBe(PaymentStatus.Failed);
        refused.StatusReason.ShouldBe("refund.do error 7");
        rejected.Status.ShouldBe(PaymentStatus.Cancelled);
        payment.PendingRefundAmount.ShouldBe(Money.Zero);
        payment.RefundableAmount.ShouldBe(Tnd(400m));
        payment.ConfirmOnlineRefund(refused, Gateway, Now).Error.ShouldBe(PaymentsErrors.RefundNotPending);
        payment.ReleaseOnlineRefund(rejected, PaymentStatus.Failed, null, Gateway, Now).Error.ShouldBe(PaymentsErrors.RefundNotPending);
        Should.Throw<ArgumentOutOfRangeException>(() => payment.ReleaseOnlineRefund(rejected, PaymentStatus.Captured, null, Gateway, Now));
    }

    [Fact]
    public void Online_refunds_follow_the_payment_they_pay_back()
    {
        var payment = OnlineConfirmed(400m, GatewayPaymentState.Deposited);
        var other = OnlineConfirmed(400m, GatewayPaymentState.Deposited);
        var refund = other.RequestOnlineRefund(Tnd(50m), "Trop-perçu", false, Manager, Now).Value;
        refund.MarkOnlineRefundSent(Now).IsSuccess.ShouldBeTrue();
        var unsent = payment.RequestOnlineRefund(Tnd(50m), "Trop-perçu", false, Manager, Now).Value;

        payment.ConfirmOnlineRefund(refund, Gateway, Now).Error.ShouldBe(PaymentsErrors.RefundMismatch);
        payment.ConfirmOnlineRefund(unsent, Gateway, Now).Error.ShouldBe(PaymentsErrors.RefundNotPending, "a refund is confirmed only once sent");
        payment.RequestOnlineRefund(Tnd(10m), " ", false, Manager, Now).Error.ShouldBe(PaymentsErrors.RefundReasonRequired);
        OnlineConfirmed(100m, GatewayPaymentState.PreAuthorized).RequestOnlineRefund(Tnd(10m), "Trop-perçu", false, Manager, Now)
            .Error.ShouldBe(PaymentsErrors.RefundNotCaptured);
        Captured(PaymentMethod.CardTerminal, 100m).RequestOnlineRefund(Tnd(10m), "Trop-perçu", false, Manager, Now).Error.ShouldBe(PaymentsErrors.RefundNotOnline);
        refund.ApproveOnlineRefund(Manager, Now).Error.ShouldBe(PaymentsErrors.RefundNotAwaitingApproval);
        payment.ApproveOnlineRefund(Manager, Now).Error.ShouldBe(PaymentsErrors.RefundNotOnline);
    }

    [Fact]
    public void The_call_log_keeps_structured_non_sensitive_fields_cut_to_their_length()
    {
        var paymentId = Guid.CreateVersion7();
        var agencyId = Guid.CreateVersion7();

        var log = GatewayCallLog.Record(new GatewayCallLogEntry(
            GatewayOperation.GetOrderStatusExtended,
            paymentId.ToString("N"),
            GatewayCallOutcome.GatewayError,
            Now,
            TimeSpan.FromMilliseconds(-5),
            GatewayOrderId: OrderId,
            HttpStatus: 200,
            GatewayErrorCode: " 6 ",
            GatewayErrorMessage: new string('m', 300),
            OrderStatus: 1,
            ActionCode: "-2007",
            CorrelationId: new string('c', 100)),
            agencyId);

        log.PaymentId.ShouldBe(paymentId);
        log.AgencyId.ShouldBe(agencyId, "the agency of the payment of the order");
        log.OrderNumber.ShouldBe(paymentId.ToString("N"));
        log.GatewayErrorCode.ShouldBe("6");
        log.GatewayErrorMessage!.Length.ShouldBe(GatewayCallLog.ErrorMessageMaxLength);
        log.CorrelationId!.Length.ShouldBe(GatewayCallLog.CorrelationIdMaxLength);
        log.DurationMilliseconds.ShouldBe(0);
        log.ActionCode.ShouldBe("-2007");
        var foreign = GatewayCallLog.Record(new GatewayCallLogEntry(GatewayOperation.Register, "not-an-id", GatewayCallOutcome.Succeeded, Now, TimeSpan.Zero), agencyId: null);
        foreign.PaymentId.ShouldBeNull();
        foreign.AgencyId.ShouldBeNull("a call for no payment of ours is a record of the platform");
        Should.Throw<ArgumentOutOfRangeException>(() =>
            GatewayCallLog.Record(new GatewayCallLogEntry((GatewayOperation)0, "x", GatewayCallOutcome.Succeeded, Now, TimeSpan.Zero), agencyId));
        Should.Throw<ArgumentException>(() =>
            GatewayCallLog.Record(new GatewayCallLogEntry(GatewayOperation.Register, "x", GatewayCallOutcome.Succeeded, Now, TimeSpan.Zero), Guid.Empty));
    }
}
