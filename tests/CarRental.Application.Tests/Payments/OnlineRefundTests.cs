using CarRental.Application.Identity.Contracts;
using CarRental.Application.Payments.Contracts;
using CarRental.Application.Payments.Online;
using CarRental.Application.Tests.Payments.Fakes;
using CarRental.Domain.Common;
using CarRental.Domain.Payments;

namespace CarRental.Application.Tests.Payments;

/// <summary>
/// Online refunds (refund.do): never more than what was captured, reserved until the gateway confirms them, and — above
/// <c>payments.online_refund_approval_threshold</c> (100.000 TND by default, refunds of the payment added up) — approved
/// by a manager first; the system actor never approves.
/// </summary>
public sealed class OnlineRefundTests
{
    private readonly OnlinePaymentsHarness _harness = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_refund_below_the_threshold_is_sent_at_once_and_confirmed_by_the_gateway()
    {
        var payment = await _harness.PaidAsync(300m, Ct);
        var agent = OnlinePaymentsHarness.User(Roles.Agent);

        var outcome = (await _harness.Online(agent).RefundAsync(new OnlineRefundRequest(payment.Id, 50m, "Geste commercial"), Ct)).Value;

        outcome.State.ShouldBe(OnlineRefundStateInfo.Refunded);
        var refund = _harness.RefundsOf(payment.Id).ShouldHaveSingleItem();
        refund.Id.ShouldBe(outcome.RefundId);
        refund.Method.ShouldBe(PaymentMethod.ClicToPay);
        refund.Status.ShouldBe(PaymentStatus.Captured);
        refund.RequiresApproval.ShouldBeFalse();
        refund.RecordedByUserId.ShouldBe(agent.UserId);
        payment.RefundedAmount.ShouldBe(Money.Tnd(50m));
        payment.PendingRefundAmount.ShouldBe(Money.Zero);
        payment.Status.ShouldBe(PaymentStatus.Captured);
        _harness.Gateway.FindOrderByNumber(payment.OrderNumber)!.RefundedAmount.ShouldBe(Money.Tnd(50m));
        _harness.CallLog.Entries.ShouldContain(e => e.Operation == GatewayOperation.Refund && e.RefundId == refund.Id && e.Amount == Money.Tnd(50m));
    }

    [Fact]
    public async Task Refunding_the_whole_payment_online_makes_it_refunded()
    {
        var payment = await _harness.PaidAsync(90m, Ct);

        (await _harness.Online(OnlinePaymentsHarness.User(Roles.Agent)).RefundAsync(new OnlineRefundRequest(payment.Id, 90m, "Annulation gratuite"), Ct))
            .Value.State.ShouldBe(OnlineRefundStateInfo.Refunded);

        payment.Status.ShouldBe(PaymentStatus.Refunded);
        _harness.Gateway.FindOrderByNumber(payment.OrderNumber)!.StatusCode.ShouldBe(FakeClicToPay.RefundedStatusCode);
    }

    [Fact]
    public async Task A_refund_above_the_threshold_waits_for_a_manager_who_approves_it()
    {
        var payment = await _harness.PaidAsync(500m, Ct);

        var outcome = (await _harness.Online(OnlinePaymentsHarness.User(Roles.Agent)).RefundAsync(new OnlineRefundRequest(payment.Id, 150m, "Annulation J-5"), Ct)).Value;

        outcome.State.ShouldBe(OnlineRefundStateInfo.AwaitingApproval);
        _harness.CallsOf(GatewayOperation.Refund, payment).ShouldBe(0, "nothing is sent before the approval");
        payment.PendingRefundAmount.ShouldBe(Money.Tnd(150m));
        payment.DomainEvents.OfType<OnlineRefundRequested>().ShouldHaveSingleItem().RequiresApproval.ShouldBeTrue();
        (await _harness.Online(OnlinePaymentsHarness.User(Roles.Agent)).ApproveRefundAsync(outcome.RefundId, Ct)).Error.ShouldBe(PaymentsErrors.RefundApprovalForbidden);

        var manager = OnlinePaymentsHarness.User(Roles.Manager);
        var approved = (await _harness.Online(manager).ApproveRefundAsync(outcome.RefundId, Ct)).Value;

        approved.State.ShouldBe(OnlineRefundStateInfo.Refunded);
        var refund = _harness.Payment(outcome.RefundId);
        refund.ApprovedByUserId.ShouldBe(manager.UserId);
        refund.ApprovedByName.ShouldBe(manager.UserName);
        payment.RefundedAmount.ShouldBe(Money.Tnd(150m));
        payment.PendingRefundAmount.ShouldBe(Money.Zero);
        (await _harness.Online(manager).ApproveRefundAsync(outcome.RefundId, Ct)).Error.ShouldBe(PaymentsErrors.RefundNotPending);
    }

    [Fact]
    public async Task A_manager_refunds_above_the_threshold_at_once_and_is_recorded_as_the_approver()
    {
        var payment = await _harness.PaidAsync(500m, Ct);
        var manager = OnlinePaymentsHarness.User(Roles.Manager);

        var outcome = (await _harness.Online(manager).RefundAsync(new OnlineRefundRequest(payment.Id, 200m, "Annulation client"), Ct)).Value;

        outcome.State.ShouldBe(OnlineRefundStateInfo.Refunded);
        var refund = _harness.Payment(outcome.RefundId);
        refund.RequiresApproval.ShouldBeTrue();
        refund.ApprovedByUserId.ShouldBe(manager.UserId);
    }

    [Fact]
    public async Task The_system_actor_never_approves_a_refund_above_the_threshold()
    {
        var payment = await _harness.PaidAsync(500m, Ct);

        var outcome = (await _harness.Online(OnlinePaymentsHarness.System()).RefundAsync(new OnlineRefundRequest(payment.Id, 300m, "Annulation automatique"), Ct)).Value;

        outcome.State.ShouldBe(OnlineRefundStateInfo.AwaitingApproval);
        (await _harness.Online(OnlinePaymentsHarness.System()).ApproveRefundAsync(outcome.RefundId, Ct)).Error.ShouldBe(PaymentsErrors.RefundApprovalForbidden);
    }

    [Fact]
    public async Task Splitting_a_refund_does_not_avoid_the_approval_and_the_threshold_is_a_setting()
    {
        var payment = await _harness.PaidAsync(500m, Ct);
        var agent = OnlinePaymentsHarness.User(Roles.Agent);

        (await _harness.Online(agent).RefundAsync(new OnlineRefundRequest(payment.Id, 60m, "Geste 1"), Ct)).Value.State.ShouldBe(OnlineRefundStateInfo.Refunded);
        (await _harness.Online(agent).RefundAsync(new OnlineRefundRequest(payment.Id, 60m, "Geste 2"), Ct)).Value.State.ShouldBe(OnlineRefundStateInfo.AwaitingApproval);

        _harness.Settings.Values[PaymentSettings.OnlineRefundApprovalThreshold.Key] = "1000.000";
        (await _harness.Online(agent).RefundAsync(new OnlineRefundRequest(payment.Id, 300m, "Annulation"), Ct)).Value.State.ShouldBe(OnlineRefundStateInfo.Refunded);
    }

    [Fact]
    public async Task A_refund_never_exceeds_what_was_captured_minus_the_refunds_in_progress()
    {
        var payment = await _harness.PaidAsync(300m, Ct);
        var agent = OnlinePaymentsHarness.User(Roles.Agent);

        (await _harness.Online(agent).RefundAsync(new OnlineRefundRequest(payment.Id, 300.001m, "Trop-perçu"), Ct)).Error.ShouldBe(PaymentsErrors.RefundExceedsCaptured);
        (await _harness.Online(agent).RefundAsync(new OnlineRefundRequest(payment.Id, 250m, "Annulation"), Ct)).Value.State.ShouldBe(OnlineRefundStateInfo.AwaitingApproval);
        (await _harness.Online(agent).RefundAsync(new OnlineRefundRequest(payment.Id, 50.001m, "Encore"), Ct)).Error.ShouldBe(PaymentsErrors.RefundExceedsCaptured);
        (await _harness.Online(agent).RefundAsync(new OnlineRefundRequest(payment.Id, 10m, " "), Ct)).Error.ShouldBe(PaymentsErrors.RefundReasonRequired);
        (await _harness.Online(agent).RefundAsync(new OnlineRefundRequest(payment.Id, 0m, "Zéro"), Ct)).Error.ShouldBe(PaymentsErrors.AmountInvalid);

        var preAuthorized = await _harness.PreAuthorizedAsync(100m, Ct);
        (await _harness.Online(agent).RefundAsync(new OnlineRefundRequest(preAuthorized.Id, 10m, "Trop tôt"), Ct)).Error.ShouldBe(PaymentsErrors.RefundNotCaptured);
    }

    [Fact]
    public async Task A_rejected_refund_releases_its_amount_and_needs_a_reason()
    {
        var payment = await _harness.PaidAsync(500m, Ct);
        var refundId = (await _harness.Online(OnlinePaymentsHarness.User(Roles.Agent)).RefundAsync(new OnlineRefundRequest(payment.Id, 400m, "Annulation"), Ct)).Value.RefundId;
        var manager = _harness.Online(OnlinePaymentsHarness.User(Roles.Manager));

        (await manager.RejectRefundAsync(refundId, " ", Ct)).Error.ShouldBe(PaymentsErrors.ReasonRequired);
        (await manager.RejectRefundAsync(refundId, "Hors politique d'annulation", Ct)).IsSuccess.ShouldBeTrue();

        var refund = _harness.Payment(refundId);
        refund.Status.ShouldBe(PaymentStatus.Cancelled);
        refund.StatusReason.ShouldBe("Hors politique d'annulation");
        payment.PendingRefundAmount.ShouldBe(Money.Zero);
        payment.RefundableAmount.ShouldBe(Money.Tnd(500m));
        _harness.CallsOf(GatewayOperation.Refund, payment).ShouldBe(0);
        (await manager.RejectRefundAsync(refundId, "Encore", Ct)).Error.ShouldBe(PaymentsErrors.RefundNotPending);
        (await _harness.Online(OnlinePaymentsHarness.User(Roles.Agent)).RejectRefundAsync(refundId, "Pas moi", Ct)).Error.ShouldBe(PaymentsErrors.RefundApprovalForbidden);
    }

    [Fact]
    public async Task A_refund_refused_by_the_gateway_is_released_and_flagged()
    {
        var payment = await _harness.PaidAsync(300m, Ct);
        _harness.Gateway.AddFault(new FakeGatewayFault(GatewayOperation.Refund, FakeFaultKind.Rejected, c => c.OrderNumber == payment.OrderNumber, "7"));

        var result = await _harness.Online(OnlinePaymentsHarness.User(Roles.Agent)).RefundAsync(new OnlineRefundRequest(payment.Id, 40m, "Geste"), Ct);

        result.Error.ShouldBe(PaymentsErrors.GatewayRejected);
        var refund = _harness.RefundsOf(payment.Id).ShouldHaveSingleItem();
        refund.Status.ShouldBe(PaymentStatus.Failed);
        payment.PendingRefundAmount.ShouldBe(Money.Zero);
        payment.RefundedAmount.ShouldBe(Money.Zero);
        payment.GatewayAttention.ShouldBe(GatewayAttention.OperationRejected);
    }

    [Fact]
    public async Task A_refund_whose_answer_was_lost_is_confirmed_by_the_refunded_amount_the_gateway_reports()
    {
        var payment = await _harness.PaidAsync(300m, Ct);
        _harness.Gateway.AddFault(new FakeGatewayFault(GatewayOperation.Refund, FakeFaultKind.LostResponse, c => c.OrderNumber == payment.OrderNumber));

        var outcome = (await _harness.Online(OnlinePaymentsHarness.User(Roles.Agent)).RefundAsync(new OnlineRefundRequest(payment.Id, 40m, "Geste"), Ct)).Value;

        outcome.State.ShouldBe(OnlineRefundStateInfo.Refunded);
        payment.RefundedAmount.ShouldBe(Money.Tnd(40m));
        _harness.CallsOf(GatewayOperation.Refund, payment).ShouldBe(1);
    }

    [Fact]
    public async Task Without_refunded_amounts_from_the_gateway_an_unanswered_refund_stays_sent_and_is_flagged_after_a_day()
    {
        var payment = await _harness.PaidAsync(300m, Ct);
        _harness.Gateway.ReportsAmountDetails = false;
        _harness.Gateway.AddFault(new FakeGatewayFault(GatewayOperation.Refund, FakeFaultKind.LostResponse, c => c.OrderNumber == payment.OrderNumber));

        var outcome = (await _harness.Online(OnlinePaymentsHarness.User(Roles.Agent)).RefundAsync(new OnlineRefundRequest(payment.Id, 40m, "Geste"), Ct)).Value;

        outcome.State.ShouldBe(OnlineRefundStateInfo.Sent);
        payment.PendingRefundAmount.ShouldBe(Money.Tnd(40m), "the amount stays reserved: never refunded twice");
        _harness.Time.Advance(TimeSpan.FromHours(25));
        await _harness.CatchUpJob().ExecuteAsync(Ct);
        payment.GatewayAttention.ShouldBe(GatewayAttention.RefundNotConfirmed);
        _harness.Payment(outcome.RefundId).Status.ShouldBe(PaymentStatus.Pending);
    }

    [Fact]
    public async Task An_acknowledged_refund_is_confirmed_even_without_refunded_amounts()
    {
        var payment = await _harness.PaidAsync(300m, Ct);
        _harness.Gateway.ReportsAmountDetails = false;

        var outcome = (await _harness.Online(OnlinePaymentsHarness.User(Roles.Agent)).RefundAsync(new OnlineRefundRequest(payment.Id, 40m, "Geste"), Ct)).Value;

        outcome.State.ShouldBe(OnlineRefundStateInfo.Refunded);
        payment.RefundedAmount.ShouldBe(Money.Tnd(40m));
    }
}
