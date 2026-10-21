using System.Globalization;
using System.Text;
using CarRental.Domain.Common;
using CarRental.Domain.Payments;

namespace CarRental.Application.Payments.Online;

/// <summary>
/// Turns a status read from the gateway into the status confirmed for a payment (invariant 7, ADR-0039). Conservative:
/// an unknown code is never a success; the only inference is a reversal the gateway acknowledged (<c>reverse.do</c>
/// answered without error) followed by a status that is neither pre-authorized nor deposited.
/// </summary>
internal static class GatewayStatusInterpreter
{
    /// <summary>The confirmed status to apply, or <see langword="null"/> when the read confirms nothing (unknown code).</summary>
    public static ConfirmedGatewayStatus? Interpret(Payment payment, GatewayOrderStatus status, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(payment);
        ArgumentNullException.ThrowIfNull(status);
        var detail = Detail(status);
        var orderId = status.GatewayOrderId;
        return status.State switch
        {
            GatewayOrderState.Registered => IsOver(payment, now)
                ? new ConfirmedGatewayStatus(GatewayPaymentState.Expired, null, orderId, detail)
                : new ConfirmedGatewayStatus(GatewayPaymentState.Registered, null, orderId, detail),
            GatewayOrderState.PreAuthorized => new ConfirmedGatewayStatus(GatewayPaymentState.PreAuthorized, status.ApprovedAmount ?? status.Amount, orderId, detail),
            GatewayOrderState.Deposited or GatewayOrderState.Refunded =>
                new ConfirmedGatewayStatus(GatewayPaymentState.Deposited, DepositedAmount(payment, status), orderId, detail),
            GatewayOrderState.Reversed => new ConfirmedGatewayStatus(GatewayPaymentState.Reversed, null, orderId, detail),
            GatewayOrderState.Declined => new ConfirmedGatewayStatus(GatewayPaymentState.Declined, null, orderId, detail),
            _ when payment.PendingGatewayOperation == PendingGatewayOperation.Reverse && payment.PendingGatewayAcknowledged =>
                new ConfirmedGatewayStatus(GatewayPaymentState.Reversed, null, orderId, detail + "; reverse.do acknowledged"),
            _ => null,
        };
    }

    /// <summary>
    /// The debited amount: reported by the gateway (<c>depositedAmount</c>), else the capture we requested (the only
    /// deposit of the order), else what is already recorded, else the amount of the order (one-step payment).
    /// </summary>
    public static Money? DepositedAmount(Payment payment, GatewayOrderStatus status)
    {
        ArgumentNullException.ThrowIfNull(payment);
        ArgumentNullException.ThrowIfNull(status);
        return status.DepositedAmount
            ?? (payment.PendingGatewayOperation == PendingGatewayOperation.Capture ? payment.PendingGatewayAmount : null)
            ?? (payment.Status is PaymentStatus.Captured or PaymentStatus.Refunded ? payment.CapturedAmount : (Money?)null)
            ?? status.Amount;
    }

    /// <summary>A non-sensitive summary of the read: status code, action code and its text.</summary>
    public static string Detail(GatewayOrderStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        var text = new StringBuilder("status ");
        text.Append(status.StatusCode is { } code ? code.ToString(CultureInfo.InvariantCulture) : "?");
        if (status.ActionCode is { Length: > 0 } action)
        {
            text.Append(", action ").Append(action);
        }

        if (status.ActionCodeDescription is { Length: > 0 } description)
        {
            text.Append(" (").Append(description).Append(')');
        }

        return text.ToString();
    }

    /// <summary>Pending past its validity: the gateway confirming it unpaid closes it as expired.</summary>
    public static bool IsOver(Payment payment, DateTimeOffset now) =>
        payment.Status == PaymentStatus.Pending && payment.OnlineExpiresAtUtc is { } expiry && now >= expiry;
}
