using CarRental.Domain.Common;

namespace CarRental.Application.Payments.Contracts;

/// <summary>How an online payment takes the money (same values as the domain enum).</summary>
public enum OnlinePaymentFlowInfo
{
    /// <summary>Pre-authorization at the submission, captured (or less) at the validation.</summary>
    PreAuthorization = 1,

    /// <summary>One-step payment (balance, payment link, fallback mode).</summary>
    OneStep = 2,
}

/// <summary>A gateway operation requested and not confirmed yet (same values as the domain enum).</summary>
public enum PendingGatewayOperationInfo
{
    Capture = 1,
    Reverse = 2,
    Refund = 3,
}

/// <summary>Why an online payment needs a look from the back office (same values as the domain enum).</summary>
public enum GatewayAttentionInfo
{
    UnknownStatus = 1,
    StatusMismatch = 2,
    AmountMismatch = 3,
    LatePayment = 4,
    CaptureNotConfirmed = 5,
    ReversalNotConfirmed = 6,
    RefundNotConfirmed = 7,
    OperationRejected = 8,
}

/// <summary>An operation of the Clic to Pay API (same values as the domain enum).</summary>
public enum GatewayOperationInfo
{
    Register = 1,
    RegisterPreAuth = 2,
    Deposit = 3,
    Reverse = 4,
    Refund = 5,
    GetOrderStatusExtended = 6,
}

/// <summary>Outcome of a call to the gateway (same values as the domain enum).</summary>
public enum GatewayCallOutcomeInfo
{
    Succeeded = 1,
    GatewayError = 2,
    Unavailable = 3,
    InvalidResponse = 4,
}

/// <summary>Where an online refund stands.</summary>
public enum OnlineRefundStateInfo
{
    /// <summary>Waits for the approval of a manager; nothing sent to the gateway yet.</summary>
    AwaitingApproval = 1,

    /// <summary>Sent to the gateway, not confirmed yet (the amount stays reserved).</summary>
    Sent = 2,

    /// <summary>Confirmed: paid back to the customer.</summary>
    Refunded = 3,

    /// <summary>Refused by the gateway; the amount was released.</summary>
    Failed = 4,

    /// <summary>Rejected by a manager; the amount was released.</summary>
    Rejected = 5,
}

/// <summary>An online payment to start for a booking.</summary>
/// <param name="BookingId">The booking paid (id only: the Payments module does not read bookings).</param>
/// <param name="AgencyId">The agency paid (active agency of the Referential module).</param>
/// <param name="Amount">Amount in TND with millimes at most (guaranteed amount, balance…).</param>
/// <param name="Culture">Culture of the customer (<c>fr</c>, <c>en</c>…): language of the payment page.</param>
/// <param name="ReturnPath">
/// Local path (<c>/fr/…</c>, never an absolute address) the browser is sent to after the payment page when paid or
/// still being confirmed; <see cref="PaymentIdPlaceholder"/> is replaced by the payment id.
/// </param>
/// <param name="FailPath">Local path the browser is sent to after a failure or an abandon (same rules).</param>
/// <param name="Description">Short non-personal description shown by the gateway (booking number), 99 characters at most.</param>
/// <param name="IsLink">A payment link to send to the customer (by e-mail or WhatsApp) rather than a redirection.</param>
/// <param name="Validity">How long the payment can be paid; the settings of the module by default.</param>
public sealed record OnlinePaymentRequest(
    Guid BookingId,
    Guid AgencyId,
    decimal Amount,
    string? Culture,
    string ReturnPath,
    string FailPath,
    string? Description = null,
    bool IsLink = false,
    TimeSpan? Validity = null)
{
    /// <summary>Token of <see cref="ReturnPath"/> and <see cref="FailPath"/> replaced by the payment id when redirecting.</summary>
    public const string PaymentIdPlaceholder = "{paymentId}";
}

/// <summary>Where to send the customer to pay.</summary>
/// <param name="PaymentId">The online payment (its id in 32 hexadecimal characters is the order number).</param>
/// <param name="OrderNumber">The order number sent to the gateway.</param>
/// <param name="RedirectUrl">The payment page of the gateway (absolute address; relative with the simulated gateway).</param>
/// <param name="ExpiresAtUtc">End of the validity of the payment.</param>
/// <param name="Reused">The page of a payment started moments ago for the same booking (double submission).</param>
public sealed record OnlinePaymentRedirect(Guid PaymentId, string OrderNumber, string RedirectUrl, DateTimeOffset ExpiresAtUtc, bool Reused);

/// <summary>The outcome of a return from the payment page.</summary>
/// <param name="PaymentId">The online payment identified.</param>
/// <param name="BookingId">Its booking.</param>
/// <param name="Status">Its status after the server-to-server read.</param>
/// <param name="StatusConfirmed">The status could be read from the gateway now (false: gateway unavailable, the catch-up job will read it).</param>
/// <param name="RedirectPath">The local path to send the browser to (validated local path, placeholders replaced).</param>
public sealed record OnlinePaymentReturn(Guid PaymentId, Guid BookingId, PaymentStatusInfo Status, bool StatusConfirmed, string RedirectPath);

/// <summary>What the return or failure address received (query values that only identify the order).</summary>
/// <param name="OrderNumber">Our order number (<c>order</c>).</param>
/// <param name="GatewayOrderId">The gateway order id (<c>orderId</c>).</param>
/// <param name="FromFailureAddress">The browser came back through the failure address.</param>
public sealed record OnlinePaymentReturnRequest(string? OrderNumber, string? GatewayOrderId, bool FromFailureAddress);

/// <summary>An online refund to request on a captured Clic to Pay payment.</summary>
/// <param name="PaymentId">The captured online payment.</param>
/// <param name="Amount">Amount in TND, at most what remains refundable.</param>
/// <param name="Reason">Why (cancellation, overpayment…): mandatory.</param>
public sealed record OnlineRefundRequest(Guid PaymentId, decimal Amount, string Reason);

/// <summary>Where a requested online refund stands right after the call.</summary>
public sealed record OnlineRefundOutcome(Guid RefundId, Guid PaymentId, OnlineRefundStateInfo State);

/// <summary>An online refund of a payment.</summary>
public sealed record OnlineRefundInfo(
    Guid RefundId,
    Guid PaymentId,
    Money Amount,
    string? Reason,
    OnlineRefundStateInfo State,
    bool RequiresApproval,
    DateTimeOffset RequestedAtUtc,
    string? RequestedByName,
    DateTimeOffset? ApprovedAtUtc,
    string? ApprovedByName,
    DateTimeOffset? SentAtUtc,
    DateTimeOffset StatusChangedAtUtc,
    string? StatusReason);

/// <summary>An online Clic to Pay payment with what the gateway confirmed and what is still in progress.</summary>
public sealed record OnlinePaymentInfo(
    Guid PaymentId,
    Guid BookingId,
    Guid AgencyId,
    string OrderNumber,
    string? GatewayOrderId,
    OnlinePaymentFlowInfo Flow,
    bool IsPaymentLink,
    PaymentStatusInfo Status,
    Money Amount,
    Money CapturedAmount,
    Money RefundedAmount,
    Money PendingRefundAmount,
    Money RefundableAmount,
    PendingGatewayOperationInfo? PendingOperation,
    Money? PendingAmount,
    DateTimeOffset? PendingSinceUtc,
    bool PendingAcknowledged,
    int? GatewayStatusCode,
    GatewayAttentionInfo? Attention,
    string? AttentionDetail,
    DateTimeOffset? AttentionAtUtc,
    string? PaymentPageUrl,
    DateTimeOffset? ExpiresAtUtc,
    DateTimeOffset? ExpiredAtUtc,
    DateTimeOffset RecordedAtUtc,
    string? RecordedByName,
    DateTimeOffset? CapturedAtUtc,
    DateTimeOffset StatusChangedAtUtc,
    string? StatusReason,
    IReadOnlyList<OnlineRefundInfo> Refunds)
{
    /// <summary>The customer has not paid yet (nothing requested from the gateway meanwhile).</summary>
    public bool IsAwaitingCustomer => Status == PaymentStatusInfo.Pending;

    /// <summary>
    /// A pre-authorization the agency can capture now, or whose capture was left without answer (sent again once the
    /// status read shows it had no effect).
    /// </summary>
    public bool CanCapture =>
        Flow == OnlinePaymentFlowInfo.PreAuthorization
        && Status == PaymentStatusInfo.PreAuthorized
        && (PendingOperation is null || (PendingOperation == PendingGatewayOperationInfo.Capture && !PendingAcknowledged));

    /// <summary>A pre-authorization (or a payment not made yet) the agency can release now.</summary>
    public bool CanReverse => Status is PaymentStatusInfo.PreAuthorized or PaymentStatusInfo.Pending && PendingOperation is null;

    /// <summary>A captured payment with something left to refund online.</summary>
    public bool CanRefund => Status == PaymentStatusInfo.Captured && RefundableAmount.IsPositive;
}

/// <summary>One call to the payment gateway, as written in the call log (no credentials, no card data, no raw answer).</summary>
public sealed record GatewayCallInfo(
    Guid Id,
    GatewayOperationInfo Operation,
    GatewayCallOutcomeInfo Outcome,
    string OrderNumber,
    string? GatewayOrderId,
    Money? Amount,
    int? HttpStatus,
    string? GatewayErrorCode,
    string? GatewayErrorMessage,
    int? OrderStatus,
    string? ActionCode,
    int DurationMilliseconds,
    DateTimeOffset StartedAtUtc,
    Guid? RefundId);
