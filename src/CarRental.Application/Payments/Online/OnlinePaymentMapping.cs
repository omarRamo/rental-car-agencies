using CarRental.Application.Payments.Contracts;
using CarRental.Domain.Payments;

namespace CarRental.Application.Payments.Online;

/// <summary>Conversions of the online payments into their contract types (same enum values).</summary>
internal static class OnlinePaymentMapping
{
    public static OnlinePaymentFlowInfo ToInfo(this OnlinePaymentFlow flow) => (OnlinePaymentFlowInfo)(int)flow;

    public static PendingGatewayOperationInfo ToInfo(this PendingGatewayOperation operation) => (PendingGatewayOperationInfo)(int)operation;

    public static GatewayAttentionInfo ToInfo(this GatewayAttention attention) => (GatewayAttentionInfo)(int)attention;

    public static GatewayOperationInfo ToInfo(this GatewayOperation operation) => (GatewayOperationInfo)(int)operation;

    public static GatewayCallOutcomeInfo ToInfo(this GatewayCallOutcome outcome) => (GatewayCallOutcomeInfo)(int)outcome;

    public static GatewayAttention ToDomain(this GatewayAttentionInfo attention) => (GatewayAttention)(int)attention;

    /// <summary>Where an online refund stands, from its status, approval and sending.</summary>
    public static OnlineRefundStateInfo RefundStateOf(Payment refund)
    {
        ArgumentNullException.ThrowIfNull(refund);
        return refund.Status switch
        {
            PaymentStatus.Captured => OnlineRefundStateInfo.Refunded,
            PaymentStatus.Failed => OnlineRefundStateInfo.Failed,
            PaymentStatus.Cancelled => OnlineRefundStateInfo.Rejected,
            _ when refund.IsAwaitingApproval => OnlineRefundStateInfo.AwaitingApproval,
            _ => OnlineRefundStateInfo.Sent,
        };
    }

    public static OnlineRefundInfo ToRefundInfo(this Payment refund)
    {
        ArgumentNullException.ThrowIfNull(refund);
        return new OnlineRefundInfo(
            refund.Id,
            refund.OriginalPaymentId ?? Guid.Empty,
            refund.Amount,
            refund.Notes,
            RefundStateOf(refund),
            refund.RequiresApproval,
            refund.RecordedAtUtc,
            refund.RecordedByName,
            refund.ApprovedAtUtc,
            refund.ApprovedByName,
            refund.PendingGatewayOperation == PendingGatewayOperation.Refund ? refund.PendingGatewaySinceUtc : null,
            refund.StatusChangedAtUtc,
            refund.StatusReason);
    }

    public static OnlinePaymentInfo ToOnlineInfo(this Payment payment, IEnumerable<Payment> onlineRefunds)
    {
        ArgumentNullException.ThrowIfNull(payment);
        ArgumentNullException.ThrowIfNull(onlineRefunds);
        return new OnlinePaymentInfo(
            payment.Id,
            payment.BookingId,
            payment.AgencyId,
            payment.OrderNumber,
            payment.ExternalReference,
            (payment.OnlineFlow ?? OnlinePaymentFlow.OneStep).ToInfo(),
            payment.IsPaymentLink,
            payment.Status.ToInfo(),
            payment.Amount,
            payment.CapturedAmount,
            payment.RefundedAmount,
            payment.PendingRefundAmount,
            payment.RefundableAmount,
            payment.PendingGatewayOperation?.ToInfo(),
            payment.PendingGatewayAmount,
            payment.PendingGatewaySinceUtc,
            payment.PendingGatewayAcknowledged,
            payment.GatewayStatusCode,
            payment.GatewayAttention?.ToInfo(),
            payment.GatewayAttentionDetail,
            payment.GatewayAttentionAtUtc,
            payment.GatewayFormUrl,
            payment.OnlineExpiresAtUtc,
            payment.ExpiredAtUtc,
            payment.RecordedAtUtc,
            payment.RecordedByName,
            payment.CapturedAtUtc,
            payment.StatusChangedAtUtc,
            payment.StatusReason,
            [.. onlineRefunds.Where(r => r.OriginalPaymentId == payment.Id).OrderBy(r => r.RecordedAtUtc).ThenBy(r => r.Id).Select(r => r.ToRefundInfo())]);
    }

    public static GatewayCallInfo ToInfo(this GatewayCallLog call)
    {
        ArgumentNullException.ThrowIfNull(call);
        return new GatewayCallInfo(
            call.Id,
            call.Operation.ToInfo(),
            call.Outcome.ToInfo(),
            call.OrderNumber,
            call.GatewayOrderId,
            call.Amount,
            call.HttpStatus,
            call.GatewayErrorCode,
            call.GatewayErrorMessage,
            call.OrderStatus,
            call.ActionCode,
            call.DurationMilliseconds,
            call.StartedAtUtc,
            call.RefundId);
    }
}
