using CarRental.Application.Common.Paging;
using CarRental.Application.Identity.Contracts;
using CarRental.Application.Payments;
using CarRental.Application.Payments.Administration;
using CarRental.Application.Payments.Contracts;
using CarRental.Application.Payments.Online;
using CarRental.Application.Tests.Payments.Fakes;
using CarRental.Domain.Common;
using CarRental.Domain.Payments;
using Microsoft.Extensions.DependencyInjection;

namespace CarRental.Application.Tests.Payments;

/// <summary>
/// The back office of the online payments: rights (read, manage, approve), lists with flags and refunds awaiting
/// approval, detail with the call history, payment links, the daily reconciliation and the registration of the module.
/// </summary>
public sealed class OnlinePaymentAdministrationTests
{
    private readonly OnlinePaymentsHarness _harness = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_preparer_sees_no_online_payment_and_an_agent_approves_nothing()
    {
        var payment = await _harness.PaidAsync(500m, Ct);
        var preparer = _harness.Admin(OnlinePaymentsHarness.User(Roles.Preparer));
        var agent = _harness.Admin(OnlinePaymentsHarness.User(Roles.Agent));
        var accountant = _harness.Admin(OnlinePaymentsHarness.User(Roles.Accountant));

        (await preparer.ListAsync(new OnlinePaymentListFilter(), new PageRequest(1, 25), Ct)).Error.ShouldBe(PaymentsErrors.Forbidden);
        (await preparer.GetAsync(payment.Id, Ct)).Error.ShouldBe(PaymentsErrors.Forbidden);
        (await preparer.RefreshAsync(payment.Id, Ct)).Error.ShouldBe(PaymentsErrors.Forbidden);
        (await preparer.CreatePaymentLinkAsync(_harness.Request(10m), Ct)).Error.ShouldBe(PaymentsErrors.Forbidden);
        var awaiting = (await accountant.RefundAsync(new OnlineRefundRequest(payment.Id, 200m, "Annulation"), Ct)).Value;
        awaiting.State.ShouldBe(OnlineRefundStateInfo.AwaitingApproval, "the accountant manages payments but does not approve refunds");
        (await agent.ApproveRefundAsync(awaiting.RefundId, Ct)).Error.ShouldBe(PaymentsErrors.RefundApprovalForbidden);
        (await agent.RejectRefundAsync(awaiting.RefundId, "Non", Ct)).Error.ShouldBe(PaymentsErrors.RefundApprovalForbidden);
        (await _harness.Admin(OnlinePaymentsHarness.User(Roles.Manager)).ApproveRefundAsync(awaiting.RefundId, Ct)).Value.State.ShouldBe(OnlineRefundStateInfo.Refunded);
    }

    [Fact]
    public async Task The_list_shows_flags_and_refunds_awaiting_approval_and_filters_on_them()
    {
        var flagged = await _harness.PreAuthorizedAsync(100m, Ct);
        flagged.FlagGatewayAttention(GatewayAttention.UnknownStatus, "status 9", _harness.Time.GetUtcNow());
        var refunded = await _harness.PaidAsync(500m, Ct);
        await _harness.Admin(OnlinePaymentsHarness.User(Roles.Agent)).RefundAsync(new OnlineRefundRequest(refunded.Id, 300m, "Annulation"), Ct);
        var admin = _harness.Admin(OnlinePaymentsHarness.User(Roles.Accountant));

        var all = (await admin.ListAsync(new OnlinePaymentListFilter(), new PageRequest(1, 25), Ct)).Value;
        var attention = (await admin.ListAsync(new OnlinePaymentListFilter(NeedsAttention: true), new PageRequest(1, 25), Ct)).Value;
        var approvals = (await admin.ListAsync(new OnlinePaymentListFilter(AwaitingApproval: true), new PageRequest(1, 25), Ct)).Value;
        var captured = (await admin.ListAsync(new OnlinePaymentListFilter(Status: PaymentStatusInfo.Captured), new PageRequest(1, 25), Ct)).Value;

        all.Page.TotalCount.ShouldBe(2);
        all.AttentionCount.ShouldBe(1);
        all.AwaitingApprovalCount.ShouldBe(1);
        attention.Page.Items.ShouldHaveSingleItem().PaymentId.ShouldBe(flagged.Id);
        var awaiting = approvals.Page.Items.ShouldHaveSingleItem();
        awaiting.PaymentId.ShouldBe(refunded.Id);
        awaiting.Refunds.ShouldHaveSingleItem().State.ShouldBe(OnlineRefundStateInfo.AwaitingApproval);
        awaiting.PendingRefundAmount.ShouldBe(Money.Tnd(300m));
        captured.Page.Items.ShouldHaveSingleItem().PaymentId.ShouldBe(refunded.Id);
    }

    [Fact]
    public async Task The_detail_shows_the_calls_to_the_gateway_without_credentials_and_a_manager_clears_a_flag()
    {
        var payment = await _harness.PreAuthorizedAsync(100m, Ct);
        payment.FlagGatewayAttention(GatewayAttention.StatusMismatch, "status 2; recorded PreAuthorized", _harness.Time.GetUtcNow());
        var admin = _harness.Admin(OnlinePaymentsHarness.User(Roles.Manager));

        var view = (await admin.GetAsync(payment.Id, Ct)).Value;
        (await admin.MarkReviewedAsync(payment.Id, Ct)).IsSuccess.ShouldBeTrue();

        view.Payment.Attention.ShouldBe(GatewayAttentionInfo.StatusMismatch);
        view.Calls.Select(c => c.Operation).ShouldBe([GatewayOperationInfo.GetOrderStatusExtended, GatewayOperationInfo.RegisterPreAuth], ignoreOrder: true);
        view.Calls.ShouldAllBe(c => c.OrderNumber == payment.OrderNumber);
        payment.GatewayAttention.ShouldBeNull();
        (await admin.GetAsync(Guid.CreateVersion7(), Ct)).Error.ShouldBe(PaymentsErrors.OnlinePaymentNotFound);
    }

    [Fact]
    public async Task A_payment_link_is_a_one_step_payment_valid_24_hours()
    {
        var bookingId = Guid.CreateVersion7();
        var admin = _harness.Admin(OnlinePaymentsHarness.User(Roles.Agent));

        var link = (await admin.CreatePaymentLinkAsync(_harness.Request(240m, bookingId), Ct)).Value;

        var payment = _harness.Payment(link.PaymentId);
        payment.IsPaymentLink.ShouldBeTrue();
        payment.OnlineFlow.ShouldBe(OnlinePaymentFlow.OneStep);
        link.ExpiresAtUtc.ShouldBe(_harness.Time.GetUtcNow().AddHours(24));
        (await admin.GetBookingAsync(bookingId, Ct)).Value.ShouldHaveSingleItem().IsPaymentLink.ShouldBeTrue();
    }

    [Fact]
    public async Task The_daily_reconciliation_flags_a_difference_with_the_gateway()
    {
        var paid = await _harness.PaidAsync(100m, Ct);
        var reversed = await _harness.PreAuthorizedAsync(80m, Ct);
        (await _harness.Online(OnlinePaymentsHarness.System()).ReverseAsync(reversed.Id, Ct)).IsSuccess.ShouldBeTrue();
        var consistent = await _harness.PaidAsync(60m, Ct);
        _harness.Gateway.OverrideStatus(paid.OrderNumber, 2, Money.Tnd(90m)).IsSuccess.ShouldBeTrue();
        _harness.Gateway.OverrideStatus(reversed.OrderNumber, 2, Money.Tnd(80m)).IsSuccess.ShouldBeTrue();

        await _harness.ReconciliationJob().ExecuteAsync(Ct);

        paid.GatewayAttention.ShouldBe(GatewayAttention.AmountMismatch);
        reversed.GatewayAttention.ShouldBe(GatewayAttention.StatusMismatch);
        reversed.Status.ShouldBe(PaymentStatus.Cancelled, "the reconciliation flags, it never reopens a reversed payment");
        consistent.GatewayAttention.ShouldBeNull();
    }

    [Fact]
    public void The_module_registers_the_online_payments_its_jobs_and_its_settings()
    {
        var services = new ServiceCollection().AddPaymentsModule();

        services.ShouldContain(d => d.ServiceType == typeof(IOnlinePayments) && d.Lifetime == ServiceLifetime.Scoped);
        services.ShouldContain(d => d.ServiceType == typeof(OnlinePaymentAdministration));
        services.ShouldContain(d => d.ServiceType == typeof(OnlinePaymentCatchUpJob));
        services.ShouldContain(d => d.ServiceType == typeof(OnlinePaymentReconciliationJob));
        OnlinePaymentCatchUpJob.CronExpression.ShouldBe("*/15 * * * *");
        OnlinePaymentCatchUpJob.JobId.ShouldBe("payments.online_catch_up");
        OnlinePaymentReconciliationJob.JobId.ShouldBe("payments.online_reconciliation");
        PaymentSettings.All.Select(s => s.Key).ShouldBe(
            ["payments.online_refund_approval_threshold", "payments.payment_link_validity", "payments.online_attempt_validity"]);
        PaymentSettings.OnlineRefundApprovalThreshold.Default.ShouldBe(Money.Tnd(100m));
        PaymentSettings.PaymentLinkValidity.Default.ShouldBe(TimeSpan.FromHours(24));
    }
}
