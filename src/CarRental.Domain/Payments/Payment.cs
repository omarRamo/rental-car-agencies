using System.Globalization;
using CarRental.Domain.Common;

namespace CarRental.Domain.Payments;

/// <summary>
/// A movement of money for a booking (aggregate): money received from the customer (<see cref="PaymentDirection.Incoming"/>)
/// or paid back (<see cref="PaymentDirection.Refund"/>, linked to the incoming payment it refunds), with its means of
/// payment, status, external reference, author and, for cash, the cash session it went through. The booking is
/// referenced by its id only.
/// </summary>
/// <remarks>
/// <para>At the counter, a payment is recorded <see cref="PaymentStatus.Captured"/> (encaissé) when the money is in hand,
/// or <see cref="PaymentStatus.Pending"/> for a bank transfer or a cheque awaiting confirmation, then confirmed or
/// cancelled. A counter refund is recorded paid back (status <see cref="PaymentStatus.Captured"/>).</para>
/// <para>Invariant 7: a <see cref="PaymentMethod.ClicToPay"/> payment becomes pre-authorized, captured, cancelled or
/// failed only through <see cref="ApplyConfirmedGatewayStatus"/>, fed exclusively from the server-to-server status check
/// (<c>getOrderStatusExtended.do</c>); every counter operation refuses it. The id of the payment, 32 hexadecimal
/// characters (<see cref="OrderNumber"/>), is the Clic to Pay order number, which makes the processing idempotent. The
/// payment also keeps what the platform needs to talk to the gateway (ADR-0038, ADR-0039): the flow, the order of the
/// gateway and its payment page, the local paths the browser returns to, the validity, the gateway operation requested and
/// not confirmed yet, the last status code read and a flag when something needs a look from the back office.</para>
/// <para>Refunds never exceed what was captured: <see cref="RefundableAmount"/> = captured − refunded − online refunds in
/// progress (<see cref="PendingRefundAmount"/>). A payment whose whole captured amount was paid back becomes
/// <see cref="PaymentStatus.Refunded"/>. An online refund is a pending refund (method Clic to Pay) whose amount is
/// reserved on the original until the gateway confirms it (paid back) or refuses it (released); above the approval
/// threshold it waits for a manager first.</para>
/// </remarks>
public sealed class Payment : AggregateRoot
{
    public const int ReferenceMaxLength = 100;
    public const int NotesMaxLength = 500;
    public const int ReasonMinLength = 3;
    public const int ReasonMaxLength = 500;

    /// <summary>Length of a local return or failure path (<see cref="GatewayReturnPath"/>).</summary>
    public const int LocalPathMaxLength = 500;

    /// <summary>Length of the address of the payment page of the gateway (<see cref="GatewayFormUrl"/>).</summary>
    public const int GatewayFormUrlMaxLength = 1000;

    /// <summary>Length of the description sent to the gateway with a registration (TODO(spec): limit of Clic to Pay).</summary>
    public const int GatewayDescriptionMaxLength = 99;

    public const int GatewayAttentionDetailMaxLength = 200;

    /// <summary>An online payment is valid at most this long (a payment link of a month is already unusual).</summary>
    public static readonly TimeSpan MaxOnlineValidity = TimeSpan.FromDays(30);

    /// <summary>Upper bound of one payment, in TND: a guard against typing errors (an extra zero), far above any rental.</summary>
    public const decimal MaxAmount = 1_000_000m;

    private Payment()
    {
    }

    private Payment(
        Guid id,
        Guid agencyId,
        Guid bookingId,
        PaymentDirection direction,
        PaymentMethod method,
        Money amount,
        AuditActor by,
        DateTimeOffset at)
        : base(id)
    {
        AgencyId = agencyId;
        BookingId = bookingId;
        Direction = direction;
        Method = method;
        Amount = amount;
        Status = PaymentStatus.Pending;
        RecordedAtUtc = at;
        RecordedByUserId = by.UserId;
        RecordedByName = PaymentText.ActorName(by);
        StatusChangedAtUtc = at;
        StatusChangedByUserId = by.UserId;
        StatusChangedByName = RecordedByName;
    }

    public Guid AgencyId { get; private set; }

    public Guid BookingId { get; private set; }

    public PaymentDirection Direction { get; private set; }

    public PaymentMethod Method { get; private set; }

    public PaymentStatus Status { get; private set; }

    /// <summary>Amount recorded: received or paid back at the counter, requested from the payment gateway online.</summary>
    public Money Amount { get; private set; }

    /// <summary>Amount actually received (paid back, for a refund); zero until captured, never above <see cref="Amount"/>.</summary>
    public Money CapturedAmount { get; private set; }

    /// <summary>Incoming payment: total of its refunds, never above <see cref="CapturedAmount"/>.</summary>
    public Money RefundedAmount { get; private set; }

    /// <summary>Incoming online payment: online refunds requested and not confirmed yet, reserved on the captured amount.</summary>
    public Money PendingRefundAmount { get; private set; }

    /// <summary>Refund: the incoming payment it pays back.</summary>
    public Guid? OriginalPaymentId { get; private set; }

    /// <summary>Cheque number, terminal receipt, transfer reference, channel booking reference or gateway order id.</summary>
    public string? ExternalReference { get; private set; }

    /// <summary>Free notes of the agent; for a refund, its reason.</summary>
    public string? Notes { get; private set; }

    /// <summary>Cash only: the cash session in which the money was received or paid back.</summary>
    public Guid? CashSessionId { get; private set; }

    public DateTimeOffset RecordedAtUtc { get; private set; }

    public Guid? RecordedByUserId { get; private set; }

    public string? RecordedByName { get; private set; }

    /// <summary>When the money was received (paid back, for a refund): the day of the daily totals.</summary>
    public DateTimeOffset? CapturedAtUtc { get; private set; }

    public DateTimeOffset StatusChangedAtUtc { get; private set; }

    public Guid? StatusChangedByUserId { get; private set; }

    public string? StatusChangedByName { get; private set; }

    /// <summary>Reason of the last status change when there is one: cancellation reason, gateway detail.</summary>
    public string? StatusReason { get; private set; }

    /// <summary>Online incoming payment: pre-authorization or one-step payment.</summary>
    public OnlinePaymentFlow? OnlineFlow { get; private set; }

    /// <summary>Online incoming payment: a payment link (sent to the customer) rather than a redirection of the browser.</summary>
    public bool IsPaymentLink { get; private set; }

    /// <summary>Online incoming payment: end of its validity; still unpaid then, it is closed as expired.</summary>
    public DateTimeOffset? OnlineExpiresAtUtc { get; private set; }

    /// <summary>Online incoming payment: when it was closed as expired (a late payment can still be recorded).</summary>
    public DateTimeOffset? ExpiredAtUtc { get; private set; }

    /// <summary>Online incoming payment: the payment page of the gateway the customer is sent to (<c>formUrl</c>).</summary>
    public string? GatewayFormUrl { get; private set; }

    /// <summary>Online incoming payment: local path the browser is sent to after the payment page (success or pending).</summary>
    public string? GatewayReturnPath { get; private set; }

    /// <summary>Online incoming payment: local path the browser is sent to after a failure or an abandon.</summary>
    public string? GatewayFailPath { get; private set; }

    /// <summary>Online incoming payment: when the gateway accepted the registration of the order.</summary>
    public DateTimeOffset? GatewayRegisteredAtUtc { get; private set; }

    /// <summary>A gateway operation requested and not confirmed yet (capture, reversal; refund on a refund).</summary>
    public PendingGatewayOperation? PendingGatewayOperation { get; private set; }

    /// <summary>Amount of the requested capture or refund.</summary>
    public Money? PendingGatewayAmount { get; private set; }

    /// <summary>When the pending gateway operation was requested (sent, for a refund).</summary>
    public DateTimeOffset? PendingGatewaySinceUtc { get; private set; }

    /// <summary>The gateway acknowledged the pending operation (error code 0); a status read must still confirm it.</summary>
    public bool PendingGatewayAcknowledged { get; private set; }

    /// <summary>Last order status code read from the gateway (raw, kept for the back office and the reconciliation).</summary>
    public int? GatewayStatusCode { get; private set; }

    /// <summary>Why the payment needs a look from the back office, if it does.</summary>
    public GatewayAttention? GatewayAttention { get; private set; }

    /// <summary>Non-sensitive detail of <see cref="GatewayAttention"/> (status code, amounts).</summary>
    public string? GatewayAttentionDetail { get; private set; }

    public DateTimeOffset? GatewayAttentionAtUtc { get; private set; }

    /// <summary>Online refund: above the approval threshold, a manager approves it before it is sent to the gateway.</summary>
    public bool RequiresApproval { get; private set; }

    public DateTimeOffset? ApprovedAtUtc { get; private set; }

    public Guid? ApprovedByUserId { get; private set; }

    public string? ApprovedByName { get; private set; }

    public bool IsOnline => Method == PaymentMethod.ClicToPay;

    /// <summary>The Clic to Pay order number of an online payment: its id in 32 hexadecimal characters.</summary>
    public string OrderNumber => Id.ToString("N");

    /// <summary>True when the money moved: captured, or captured then fully refunded.</summary>
    public bool IsSettled => Status is PaymentStatus.Captured or PaymentStatus.Refunded;

    /// <summary>What can still be paid back on this incoming payment: captured minus refunded minus online refunds in progress.</summary>
    public Money RefundableAmount =>
        Direction == PaymentDirection.Incoming && Status == PaymentStatus.Captured
            ? CapturedAmount - RefundedAmount - PendingRefundAmount
            : Money.Zero;

    /// <summary>An online refund waiting for the approval of a manager (nothing sent to the gateway yet).</summary>
    public bool IsAwaitingApproval =>
        IsOnline && Direction == PaymentDirection.Refund && Status == PaymentStatus.Pending && RequiresApproval && ApprovedAtUtc is null;

    private bool IsOnlineIncoming => IsOnline && Direction == PaymentDirection.Incoming;

    private bool IsExpiredUnpaid => Status == PaymentStatus.Cancelled && ExpiredAtUtc is not null;

    /// <summary>
    /// Records money received at the counter: captured at once, or pending for a bank transfer or a cheque awaiting
    /// confirmation. Cash needs the open cash session of the agent; a cheque, a transfer or a channel prepayment needs
    /// its reference.
    /// </summary>
    public static Result<Payment> RecordCounter(CounterPaymentDetails details, AuditActor by, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(details);
        ArgumentNullException.ThrowIfNull(by);
        if (details.AgencyId == Guid.Empty)
        {
            return PaymentsErrors.AgencyRequired;
        }

        if (details.BookingId == Guid.Empty)
        {
            return PaymentsErrors.BookingRequired;
        }

        if (!Enum.IsDefined(details.Method))
        {
            return PaymentsErrors.MethodInvalid;
        }

        if (!PaymentMethods.IsCounter(details.Method))
        {
            return PaymentsErrors.MethodNotCounter;
        }

        if (!PaymentText.IsValidAmount(details.Amount, MaxAmount))
        {
            return PaymentsErrors.AmountInvalid;
        }

        if (details.AwaitingConfirmation && !PaymentMethods.CanAwaitConfirmation(details.Method))
        {
            return PaymentsErrors.ConfirmationNotApplicable;
        }

        var reference = ValidateReference(details.Method, details.ExternalReference);
        if (reference.IsFailure)
        {
            return reference.Error;
        }

        var notes = PaymentText.Clean(details.Notes);
        if (notes is { Length: > NotesMaxLength })
        {
            return PaymentsErrors.NotesTooLong;
        }

        var session = ValidateCashSession(details.Method, details.CashSessionId);
        if (session.IsFailure)
        {
            return session.Error;
        }

        var payment = new Payment(NewId(), details.AgencyId, details.BookingId, PaymentDirection.Incoming, details.Method, details.Amount, by, at)
        {
            ExternalReference = reference.Value,
            Notes = notes,
            CashSessionId = details.CashSessionId,
        };
        if (!details.AwaitingConfirmation)
        {
            payment.Capture(details.Amount, at);
            payment.Status = PaymentStatus.Captured;
        }

        payment.Raise(new PaymentRecorded(payment.Id, payment.BookingId, payment.AgencyId, payment.Direction, payment.Method, payment.Status));
        return payment;
    }

    /// <summary>
    /// Starts an online Clic to Pay payment: pending until the payment gateway confirms its status server to server
    /// (<see cref="ApplyConfirmedGatewayStatus"/>). The return and failure paths are local paths of the site (never an
    /// open redirect); the payment can be paid until <see cref="OnlinePaymentDetails.ExpiresAtUtc"/>.
    /// </summary>
    public static Result<Payment> StartOnline(OnlinePaymentDetails details, AuditActor by, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(details);
        ArgumentNullException.ThrowIfNull(by);
        if (details.AgencyId == Guid.Empty)
        {
            return PaymentsErrors.AgencyRequired;
        }

        if (details.BookingId == Guid.Empty)
        {
            return PaymentsErrors.BookingRequired;
        }

        if (!PaymentText.IsValidAmount(details.Amount, MaxAmount))
        {
            return PaymentsErrors.AmountInvalid;
        }

        if (!Enum.IsDefined(details.Flow))
        {
            return PaymentsErrors.OnlineFlowInvalid;
        }

        var returnPath = PaymentText.Clean(details.ReturnPath);
        var failPath = PaymentText.Clean(details.FailPath);
        if (!PaymentText.IsLocalPath(returnPath, LocalPathMaxLength) || !PaymentText.IsLocalPath(failPath, LocalPathMaxLength))
        {
            return PaymentsErrors.OnlinePathInvalid;
        }

        if (details.ExpiresAtUtc <= at || details.ExpiresAtUtc - at > MaxOnlineValidity)
        {
            return PaymentsErrors.OnlineExpiryInvalid;
        }

        var payment = new Payment(NewId(), details.AgencyId, details.BookingId, PaymentDirection.Incoming, PaymentMethod.ClicToPay, details.Amount, by, at)
        {
            OnlineFlow = details.Flow,
            IsPaymentLink = details.IsLink,
            OnlineExpiresAtUtc = details.ExpiresAtUtc,
            GatewayReturnPath = returnPath,
            GatewayFailPath = failPath,
        };
        payment.Raise(new PaymentRecorded(payment.Id, payment.BookingId, payment.AgencyId, payment.Direction, payment.Method, payment.Status));
        return payment;
    }

    /// <summary>
    /// Confirms a pending counter payment once the money is there (transfer received, cheque cleared). The reference can
    /// be completed at this point.
    /// </summary>
    public Result Confirm(string? externalReference, AuditActor by, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(by);
        if (IsOnline)
        {
            return PaymentsErrors.OnlineStatusFromGatewayOnly;
        }

        if (Status != PaymentStatus.Pending)
        {
            return PaymentsErrors.PaymentNotPending;
        }

        var reference = ValidateReference(Method, PaymentText.Clean(externalReference) ?? ExternalReference);
        if (reference.IsFailure)
        {
            return reference.Error;
        }

        ExternalReference = reference.Value;
        Capture(Amount, at);
        TransitionTo(PaymentStatus.Captured, by, at, reason: null);
        return Result.Success();
    }

    /// <summary>Cancels a pending counter payment (the transfer never came, the cheque was given back), with a reason.</summary>
    public Result Cancel(string? reason, AuditActor by, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(by);
        if (IsOnline)
        {
            return PaymentsErrors.OnlineStatusFromGatewayOnly;
        }

        if (Status != PaymentStatus.Pending)
        {
            return PaymentsErrors.PaymentNotPending;
        }

        var cleaned = ValidateReason(reason, PaymentsErrors.ReasonRequired);
        if (cleaned.IsFailure)
        {
            return cleaned.Error;
        }

        TransitionTo(PaymentStatus.Cancelled, by, at, cleaned.Value);
        return Result.Success();
    }

    /// <summary>
    /// Pays money back on this captured incoming payment, at the counter (cash, card terminal, cheque or transfer), with a
    /// reason. Returns the refund, a new payment to save with this one; refunds never exceed what was captured, and a
    /// payment refunded in full becomes <see cref="PaymentStatus.Refunded"/>.
    /// </summary>
    public Result<Payment> RecordRefund(CounterRefundDetails details, AuditActor by, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(details);
        ArgumentNullException.ThrowIfNull(by);
        if (Direction != PaymentDirection.Incoming)
        {
            return PaymentsErrors.RefundNotAllowed;
        }

        if (Status == PaymentStatus.Refunded)
        {
            return PaymentsErrors.RefundExceedsCaptured;
        }

        if (Status != PaymentStatus.Captured)
        {
            return PaymentsErrors.RefundNotCaptured;
        }

        if (!Enum.IsDefined(details.Method) || !PaymentMethods.CanRefund(details.Method))
        {
            return PaymentsErrors.RefundMethodInvalid;
        }

        if (!PaymentText.IsValidAmount(details.Amount, MaxAmount))
        {
            return PaymentsErrors.AmountInvalid;
        }

        if (details.Amount > RefundableAmount)
        {
            return PaymentsErrors.RefundExceedsCaptured;
        }

        var reason = ValidateReason(details.Reason, PaymentsErrors.RefundReasonRequired);
        if (reason.IsFailure)
        {
            return reason.Error;
        }

        var reference = ValidateReference(details.Method, details.ExternalReference);
        if (reference.IsFailure)
        {
            return reference.Error;
        }

        var session = ValidateCashSession(details.Method, details.CashSessionId);
        if (session.IsFailure)
        {
            return session.Error;
        }

        var refund = new Payment(NewId(), AgencyId, BookingId, PaymentDirection.Refund, details.Method, details.Amount, by, at)
        {
            OriginalPaymentId = Id,
            ExternalReference = reference.Value,
            Notes = reason.Value,
            CashSessionId = details.CashSessionId,
        };
        refund.Capture(details.Amount, at);
        refund.Status = PaymentStatus.Captured;
        refund.Raise(new PaymentRecorded(refund.Id, BookingId, AgencyId, refund.Direction, refund.Method, refund.Status));

        RefundedAmount += details.Amount;
        Raise(new PaymentRefundRecorded(Id, refund.Id, BookingId));
        if (RefundedAmount == CapturedAmount)
        {
            TransitionTo(PaymentStatus.Refunded, by, at, reason: null);
        }

        return refund;
    }

    /// <summary>
    /// Records the order the payment gateway registered for this online payment (<c>register.do</c> or
    /// <c>registerPreAuth.do</c> answered server to server): its id becomes the external reference and its payment page
    /// the address the customer is sent to. Recording the same order again is accepted; another order is refused.
    /// </summary>
    public Result RecordGatewayRegistration(string? gatewayOrderId, string? formUrl, DateTimeOffset at)
    {
        if (!IsOnlineIncoming)
        {
            return PaymentsErrors.PaymentNotOnline;
        }

        var orderId = PaymentText.Clean(gatewayOrderId);
        if (orderId is null)
        {
            return PaymentsErrors.GatewayInvalidResponse;
        }

        if (orderId.Length > ReferenceMaxLength)
        {
            return PaymentsErrors.ReferenceTooLong;
        }

        var page = PaymentText.Clean(formUrl);
        if (page is null || page.Length > GatewayFormUrlMaxLength)
        {
            return PaymentsErrors.OnlineFormUrlInvalid;
        }

        if (ExternalReference is { } known && !string.Equals(known, orderId, StringComparison.Ordinal))
        {
            return PaymentsErrors.OnlineGatewayOrderMismatch;
        }

        if (Status != PaymentStatus.Pending && GatewayFormUrl is null)
        {
            return PaymentsErrors.GatewayTransitionInvalid;
        }

        ExternalReference = orderId;
        GatewayFormUrl ??= page;
        GatewayRegisteredAtUtc ??= at;
        return Result.Success();
    }

    /// <summary>
    /// Applies the status of this Clic to Pay payment as confirmed by the payment gateway, server to server. This is the
    /// only way an online payment becomes pre-authorized, captured, cancelled or failed (invariant 7): never call it
    /// with what the browser of the customer reports (return URL parameters prove nothing). Replaying the current status
    /// is accepted (idempotent processing of the same order). A confirmed status resolves the pending gateway operation it
    /// completes (a deposit resolves a capture, a reversal resolves a reversal…).
    /// </summary>
    public Result ApplyConfirmedGatewayStatus(ConfirmedGatewayStatus confirmed, AuditActor by, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(confirmed);
        ArgumentNullException.ThrowIfNull(by);
        if (!IsOnlineIncoming)
        {
            return PaymentsErrors.PaymentNotOnline;
        }

        var orderId = PaymentText.Clean(confirmed.GatewayOrderId);
        if (orderId is { Length: > ReferenceMaxLength })
        {
            return PaymentsErrors.ReferenceTooLong;
        }

        var outcome = confirmed.State switch
        {
            GatewayPaymentState.Registered => Status == PaymentStatus.Pending || IsExpiredUnpaid ? Result.Success() : PaymentsErrors.GatewayTransitionInvalid,
            GatewayPaymentState.PreAuthorized => PreAuthorize(confirmed, by, at),
            GatewayPaymentState.Deposited => CaptureOnline(confirmed, by, at),
            GatewayPaymentState.Reversed => Reverse(confirmed, by, at),
            GatewayPaymentState.Declined => Decline(confirmed, by, at),
            GatewayPaymentState.Expired => Expire(confirmed, by, at),
            _ => PaymentsErrors.GatewayTransitionInvalid,
        };

        if (outcome.IsSuccess && orderId is not null)
        {
            ExternalReference ??= orderId;
        }

        return outcome;
    }

    /// <summary>
    /// Marks a capture of this pre-authorization (<c>deposit.do</c>, at most the pre-authorized amount) as requested,
    /// before the call to the gateway: a second operation cannot start until a status read resolves it.
    /// </summary>
    public Result RequestGatewayCapture(Money amount, DateTimeOffset at)
    {
        if (!IsOnlineIncoming)
        {
            return PaymentsErrors.PaymentNotOnline;
        }

        if (OnlineFlow != OnlinePaymentFlow.PreAuthorization)
        {
            return PaymentsErrors.OnlineCaptureNotApplicable;
        }

        if (Status != PaymentStatus.PreAuthorized)
        {
            return PaymentsErrors.OnlineNotPreAuthorized;
        }

        if (PendingGatewayOperation is not null)
        {
            return PaymentsErrors.OnlineOperationInProgress;
        }

        if (amount.Currency != Currency.Tnd || !amount.IsPositive || amount > Amount)
        {
            return PaymentsErrors.OnlineCaptureExceedsAuthorization;
        }

        StartGatewayOperation(Payments.PendingGatewayOperation.Capture, amount, at);
        return Result.Success();
    }

    /// <summary>
    /// Marks a reversal as requested: of the pre-authorization (<c>reverse.do</c>), or, while the customer has not paid
    /// yet, a release to perform as soon as the gateway confirms a pre-authorization. Requesting it again is accepted.
    /// </summary>
    public Result RequestGatewayReverse(DateTimeOffset at)
    {
        if (!IsOnlineIncoming)
        {
            return PaymentsErrors.PaymentNotOnline;
        }

        if (Status is PaymentStatus.Captured or PaymentStatus.Refunded)
        {
            return PaymentsErrors.OnlineAlreadyCaptured;
        }

        if (Status is not (PaymentStatus.Pending or PaymentStatus.PreAuthorized))
        {
            return PaymentsErrors.GatewayTransitionInvalid;
        }

        if (PendingGatewayOperation == Payments.PendingGatewayOperation.Reverse)
        {
            return Result.Success();
        }

        if (PendingGatewayOperation is not null)
        {
            return PaymentsErrors.OnlineOperationInProgress;
        }

        StartGatewayOperation(Payments.PendingGatewayOperation.Reverse, amount: null, at);
        return Result.Success();
    }

    /// <summary>
    /// Restarts the pending reversal or capture (sent again after a status read showed the previous request had no
    /// effect): it is acknowledged afresh by the next answer of the gateway.
    /// </summary>
    public Result RestartGatewayOperation(DateTimeOffset at)
    {
        if (PendingGatewayOperation is not { } operation || operation == Payments.PendingGatewayOperation.Refund)
        {
            return PaymentsErrors.GatewayTransitionInvalid;
        }

        StartGatewayOperation(operation, PendingGatewayAmount, at);
        return Result.Success();
    }

    /// <summary>The gateway acknowledged the pending operation (error code 0); a status read still has to confirm it.</summary>
    public Result AcknowledgeGatewayOperation(PendingGatewayOperation operation)
    {
        if (!IsOnline || PendingGatewayOperation != operation)
        {
            return PaymentsErrors.GatewayTransitionInvalid;
        }

        PendingGatewayAcknowledged = true;
        return Result.Success();
    }

    /// <summary>
    /// Gives up the pending capture or reversal (refused by the gateway, or not done according to its status), with an
    /// optional flag for the back office.
    /// </summary>
    public Result AbandonGatewayOperation(PendingGatewayOperation operation, GatewayAttention? attention, string? detail, DateTimeOffset at)
    {
        if (!IsOnlineIncoming || PendingGatewayOperation != operation)
        {
            return PaymentsErrors.GatewayTransitionInvalid;
        }

        ClearGatewayOperation();
        if (attention is { } reason)
        {
            FlagGatewayAttention(reason, detail, at);
        }

        return Result.Success();
    }

    /// <summary>Keeps the last order status code read from the gateway (unchanged values are not rewritten).</summary>
    public Result RecordGatewayStatusCode(int? statusCode)
    {
        if (!IsOnlineIncoming)
        {
            return PaymentsErrors.PaymentNotOnline;
        }

        if (statusCode is not null && statusCode != GatewayStatusCode)
        {
            GatewayStatusCode = statusCode;
        }

        return Result.Success();
    }

    /// <summary>Flags the payment for the back office (mismatch with the gateway, unknown status, unconfirmed operation).</summary>
    public Result FlagGatewayAttention(GatewayAttention attention, string? detail, DateTimeOffset at)
    {
        if (!IsOnlineIncoming)
        {
            return PaymentsErrors.PaymentNotOnline;
        }

        if (!Enum.IsDefined(attention))
        {
            throw new ArgumentOutOfRangeException(nameof(attention), attention, "Unknown gateway attention.");
        }

        var cleaned = PaymentText.Truncate(detail, GatewayAttentionDetailMaxLength);
        if (GatewayAttention == attention && string.Equals(GatewayAttentionDetail, cleaned, StringComparison.Ordinal))
        {
            return Result.Success();
        }

        GatewayAttention = attention;
        GatewayAttentionDetail = cleaned;
        GatewayAttentionAtUtc = at;
        return Result.Success();
    }

    /// <summary>Clears the flag once the gateway and the platform agree again.</summary>
    public Result ClearGatewayAttention()
    {
        if (!IsOnlineIncoming)
        {
            return PaymentsErrors.PaymentNotOnline;
        }

        GatewayAttention = null;
        GatewayAttentionDetail = null;
        GatewayAttentionAtUtc = null;
        return Result.Success();
    }

    /// <summary>
    /// Requests an online refund (<c>refund.do</c>) on this captured Clic to Pay payment, with a reason. Returns the
    /// refund, a pending payment of method Clic to Pay to save with this one; its amount is reserved here until the gateway
    /// confirms or refuses it. When <paramref name="requiresApproval"/>, it waits for a manager before being sent.
    /// </summary>
    public Result<Payment> RequestOnlineRefund(Money amount, string? reason, bool requiresApproval, AuditActor by, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(by);
        if (Direction != PaymentDirection.Incoming)
        {
            return PaymentsErrors.RefundNotAllowed;
        }

        if (!IsOnline)
        {
            return PaymentsErrors.RefundNotOnline;
        }

        if (Status == PaymentStatus.Refunded)
        {
            return PaymentsErrors.RefundExceedsCaptured;
        }

        if (Status != PaymentStatus.Captured)
        {
            return PaymentsErrors.RefundNotCaptured;
        }

        if (!PaymentText.IsValidAmount(amount, MaxAmount))
        {
            return PaymentsErrors.AmountInvalid;
        }

        if (amount > RefundableAmount)
        {
            return PaymentsErrors.RefundExceedsCaptured;
        }

        var cleaned = ValidateReason(reason, PaymentsErrors.RefundReasonRequired);
        if (cleaned.IsFailure)
        {
            return cleaned.Error;
        }

        var refund = new Payment(NewId(), AgencyId, BookingId, PaymentDirection.Refund, PaymentMethod.ClicToPay, amount, by, at)
        {
            OriginalPaymentId = Id,
            Notes = cleaned.Value,
            ExternalReference = ExternalReference,
            RequiresApproval = requiresApproval,
        };
        refund.Raise(new PaymentRecorded(refund.Id, BookingId, AgencyId, refund.Direction, refund.Method, refund.Status));

        PendingRefundAmount += amount;
        Raise(new OnlineRefundRequested(Id, refund.Id, BookingId, requiresApproval));
        return refund;
    }

    /// <summary>A manager approves this online refund waiting for approval (it can then be sent to the gateway).</summary>
    public Result ApproveOnlineRefund(AuditActor by, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(by);
        if (!IsOnline || Direction != PaymentDirection.Refund)
        {
            return PaymentsErrors.RefundNotOnline;
        }

        if (Status != PaymentStatus.Pending)
        {
            return PaymentsErrors.RefundNotPending;
        }

        if (!IsAwaitingApproval)
        {
            return PaymentsErrors.RefundNotAwaitingApproval;
        }

        ApprovedAtUtc = at;
        ApprovedByUserId = by.UserId;
        ApprovedByName = PaymentText.ActorName(by);
        return Result.Success();
    }

    /// <summary>This online refund is sent to the gateway (<c>refund.do</c>): approved first when it requires it.</summary>
    public Result MarkOnlineRefundSent(DateTimeOffset at)
    {
        if (!IsOnline || Direction != PaymentDirection.Refund)
        {
            return PaymentsErrors.RefundNotOnline;
        }

        if (Status != PaymentStatus.Pending)
        {
            return PaymentsErrors.RefundNotPending;
        }

        if (IsAwaitingApproval)
        {
            return PaymentsErrors.RefundApprovalRequired;
        }

        if (PendingGatewayOperation == Payments.PendingGatewayOperation.Refund)
        {
            return PaymentsErrors.OnlineOperationInProgress;
        }

        StartGatewayOperation(Payments.PendingGatewayOperation.Refund, Amount, at);
        return Result.Success();
    }

    /// <summary>
    /// The gateway confirmed the online refund <paramref name="refund"/> of this payment: the refund is paid back, its
    /// reserved amount becomes refunded, and the payment becomes <see cref="PaymentStatus.Refunded"/> when nothing is left.
    /// Confirming it again is accepted.
    /// </summary>
    public Result ConfirmOnlineRefund(Payment refund, AuditActor by, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(refund);
        ArgumentNullException.ThrowIfNull(by);
        var check = CheckOnlineRefund(refund);
        if (check.IsFailure)
        {
            return check;
        }

        if (refund.Status == PaymentStatus.Captured)
        {
            return Result.Success();
        }

        if (refund.Status != PaymentStatus.Pending || refund.PendingGatewayOperation != Payments.PendingGatewayOperation.Refund)
        {
            return PaymentsErrors.RefundNotPending;
        }

        refund.ClearGatewayOperation();
        refund.Capture(refund.Amount, at);
        refund.TransitionTo(PaymentStatus.Captured, by, at, reason: null);

        PendingRefundAmount -= refund.Amount;
        RefundedAmount += refund.Amount;
        Raise(new PaymentRefundRecorded(Id, refund.Id, BookingId));
        if (RefundedAmount == CapturedAmount && Status == PaymentStatus.Captured)
        {
            TransitionTo(PaymentStatus.Refunded, by, at, reason: null);
        }

        return Result.Success();
    }

    /// <summary>
    /// Releases the amount reserved by the online refund <paramref name="refund"/>: refused by the gateway
    /// (<see cref="PaymentStatus.Failed"/>) or rejected by a manager (<see cref="PaymentStatus.Cancelled"/>), with a reason.
    /// Releasing it again with the same outcome is accepted.
    /// </summary>
    public Result ReleaseOnlineRefund(Payment refund, PaymentStatus outcome, string? reason, AuditActor by, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(refund);
        ArgumentNullException.ThrowIfNull(by);
        if (outcome is not (PaymentStatus.Failed or PaymentStatus.Cancelled))
        {
            throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "An online refund is released as failed or cancelled.");
        }

        var check = CheckOnlineRefund(refund);
        if (check.IsFailure)
        {
            return check;
        }

        if (refund.Status == outcome)
        {
            return Result.Success();
        }

        if (refund.Status != PaymentStatus.Pending)
        {
            return PaymentsErrors.RefundNotPending;
        }

        refund.ClearGatewayOperation();
        refund.TransitionTo(outcome, by, at, PaymentText.Truncate(reason, ReasonMaxLength));
        PendingRefundAmount -= refund.Amount;
        return Result.Success();
    }

    private static Result<string?> ValidateReference(PaymentMethod method, string? externalReference)
    {
        var reference = PaymentText.Clean(externalReference);
        if (reference is { Length: > ReferenceMaxLength })
        {
            return PaymentsErrors.ReferenceTooLong;
        }

        if (reference is null && PaymentMethods.RequiresReference(method))
        {
            return PaymentsErrors.ReferenceRequired;
        }

        return Result.Success(reference);
    }

    private static Result<string> ValidateReason(string? reason, Error whenMissing)
    {
        var cleaned = PaymentText.Clean(reason);
        if (cleaned is null || cleaned.Length < ReasonMinLength)
        {
            return whenMissing;
        }

        return cleaned.Length > ReasonMaxLength ? PaymentsErrors.ReasonTooLong : cleaned;
    }

    /// <summary>Cash always goes through a cash session; any other means never does (passing one is a bug).</summary>
    private static Result ValidateCashSession(PaymentMethod method, Guid? cashSessionId)
    {
        if (method == PaymentMethod.Cash)
        {
            return cashSessionId is { } id && id != Guid.Empty ? Result.Success() : PaymentsErrors.CashSessionRequired;
        }

        return cashSessionId is null
            ? Result.Success()
            : throw new ArgumentException("Only cash is recorded in a cash session.", nameof(cashSessionId));
    }

    private static string? GatewayDetail(ConfirmedGatewayStatus confirmed) => PaymentText.Truncate(confirmed.Detail, ReasonMaxLength);

    private Result CheckOnlineRefund(Payment refund)
    {
        if (!IsOnlineIncoming || !refund.IsOnline || refund.Direction != PaymentDirection.Refund)
        {
            return PaymentsErrors.RefundNotOnline;
        }

        return refund.OriginalPaymentId == Id ? Result.Success() : PaymentsErrors.RefundMismatch;
    }

    private Result PreAuthorize(ConfirmedGatewayStatus confirmed, AuditActor by, DateTimeOffset at)
    {
        if (Status == PaymentStatus.PreAuthorized)
        {
            return Result.Success();
        }

        if (Status != PaymentStatus.Pending && !IsExpiredUnpaid)
        {
            return PaymentsErrors.GatewayTransitionInvalid;
        }

        if (confirmed.Amount != Amount)
        {
            return PaymentsErrors.GatewayAmountInvalid;
        }

        var late = IsExpiredUnpaid;
        TransitionTo(PaymentStatus.PreAuthorized, by, at, GatewayDetail(confirmed));
        if (late)
        {
            FlagGatewayAttention(Payments.GatewayAttention.LatePayment, GatewayDetail(confirmed), at);
        }

        return Result.Success();
    }

    private Result CaptureOnline(ConfirmedGatewayStatus confirmed, AuditActor by, DateTimeOffset at)
    {
        if (Status is PaymentStatus.Captured or PaymentStatus.Refunded)
        {
            return confirmed.Amount == CapturedAmount ? Result.Success() : PaymentsErrors.GatewayAmountInvalid;
        }

        if (Status is not (PaymentStatus.Pending or PaymentStatus.PreAuthorized) && !IsExpiredUnpaid)
        {
            return PaymentsErrors.GatewayTransitionInvalid;
        }

        if (confirmed.Amount is not { } deposited || !PaymentText.IsValidAmount(deposited, MaxAmount) || deposited > Amount)
        {
            return PaymentsErrors.GatewayAmountInvalid;
        }

        var late = IsExpiredUnpaid;
        var requested = PendingGatewayOperation == Payments.PendingGatewayOperation.Capture ? PendingGatewayAmount : null;
        var reversalWanted = PendingGatewayOperation == Payments.PendingGatewayOperation.Reverse;
        ClearGatewayOperation();
        Capture(deposited, at);
        TransitionTo(PaymentStatus.Captured, by, at, GatewayDetail(confirmed));
        if (late)
        {
            FlagGatewayAttention(Payments.GatewayAttention.LatePayment, GatewayDetail(confirmed), at);
        }
        else if (reversalWanted)
        {
            FlagGatewayAttention(Payments.GatewayAttention.StatusMismatch, "deposited while a reversal was requested", at);
        }
        else if (requested is { } expected && expected != deposited)
        {
            FlagGatewayAttention(
                Payments.GatewayAttention.AmountMismatch,
                string.Create(CultureInfo.InvariantCulture, $"requested {expected:F}, deposited {deposited:F}"),
                at);
        }

        return Result.Success();
    }

    private Result Reverse(ConfirmedGatewayStatus confirmed, AuditActor by, DateTimeOffset at)
    {
        if (Status == PaymentStatus.Cancelled)
        {
            return Result.Success();
        }

        if (Status is not (PaymentStatus.Pending or PaymentStatus.PreAuthorized))
        {
            return PaymentsErrors.GatewayTransitionInvalid;
        }

        var captureWanted = PendingGatewayOperation == Payments.PendingGatewayOperation.Capture;
        ClearGatewayOperation();
        TransitionTo(PaymentStatus.Cancelled, by, at, GatewayDetail(confirmed));
        if (captureWanted)
        {
            FlagGatewayAttention(Payments.GatewayAttention.StatusMismatch, "reversed while a capture was requested", at);
        }

        return Result.Success();
    }

    private Result Decline(ConfirmedGatewayStatus confirmed, AuditActor by, DateTimeOffset at)
    {
        if (Status == PaymentStatus.Failed || IsExpiredUnpaid)
        {
            return Result.Success();
        }

        if (Status != PaymentStatus.Pending)
        {
            return PaymentsErrors.GatewayTransitionInvalid;
        }

        ClearGatewayOperation();
        TransitionTo(PaymentStatus.Failed, by, at, GatewayDetail(confirmed));
        return Result.Success();
    }

    private Result Expire(ConfirmedGatewayStatus confirmed, AuditActor by, DateTimeOffset at)
    {
        if (IsExpiredUnpaid)
        {
            return Result.Success();
        }

        if (Status != PaymentStatus.Pending)
        {
            return PaymentsErrors.GatewayTransitionInvalid;
        }

        if (OnlineExpiresAtUtc is not { } expiry || at < expiry)
        {
            return PaymentsErrors.OnlineNotExpired;
        }

        ClearGatewayOperation();
        ExpiredAtUtc = at;
        TransitionTo(PaymentStatus.Cancelled, by, at, GatewayDetail(confirmed) ?? "expired");
        Raise(new OnlinePaymentExpired(Id, BookingId));
        return Result.Success();
    }

    private void StartGatewayOperation(PendingGatewayOperation operation, Money? amount, DateTimeOffset at)
    {
        PendingGatewayOperation = operation;
        PendingGatewayAmount = amount;
        PendingGatewaySinceUtc = at;
        PendingGatewayAcknowledged = false;
    }

    private void ClearGatewayOperation()
    {
        PendingGatewayOperation = null;
        PendingGatewayAmount = null;
        PendingGatewaySinceUtc = null;
        PendingGatewayAcknowledged = false;
    }

    private void Capture(Money amount, DateTimeOffset at)
    {
        CapturedAmount = amount;
        CapturedAtUtc = at;
    }

    private void TransitionTo(PaymentStatus status, AuditActor by, DateTimeOffset at, string? reason)
    {
        var previous = Status;
        Status = status;
        StatusChangedAtUtc = at;
        StatusChangedByUserId = by.UserId;
        StatusChangedByName = PaymentText.ActorName(by);
        StatusReason = reason;
        Raise(new PaymentStatusChanged(Id, BookingId, Direction, previous, status));
    }
}

/// <summary>
/// What an agent enters to record money received at the counter. <c>CashSessionId</c> is the open cash session of the
/// agent: required for cash, absent otherwise. <c>AwaitingConfirmation</c> records a bank transfer or a cheque before the
/// money is confirmed (pending).
/// </summary>
public sealed record CounterPaymentDetails(
    Guid AgencyId,
    Guid BookingId,
    PaymentMethod Method,
    Money Amount,
    string? ExternalReference = null,
    string? Notes = null,
    Guid? CashSessionId = null,
    bool AwaitingConfirmation = false);

/// <summary>
/// What an agent enters to pay money back at the counter. <c>CashSessionId</c> is the open cash session of the agent:
/// required for a cash refund, absent otherwise.
/// </summary>
public sealed record CounterRefundDetails(
    PaymentMethod Method,
    Money Amount,
    string Reason,
    string? ExternalReference = null,
    Guid? CashSessionId = null);

/// <summary>An online Clic to Pay payment to start for a booking.</summary>
/// <param name="AgencyId">The agency paid.</param>
/// <param name="BookingId">The booking paid (id only).</param>
/// <param name="Amount">Amount requested from the gateway (pre-authorized, or paid in one step).</param>
/// <param name="Flow">Pre-authorization or one-step payment.</param>
/// <param name="ReturnPath">Local path the browser is sent to after the payment page (success or pending).</param>
/// <param name="FailPath">Local path the browser is sent to after a failure or an abandon.</param>
/// <param name="ExpiresAtUtc">End of the validity: still unpaid then, the payment is closed as expired.</param>
/// <param name="IsLink">A payment link sent to the customer rather than a redirection of the browser.</param>
public sealed record OnlinePaymentDetails(
    Guid AgencyId,
    Guid BookingId,
    Money Amount,
    OnlinePaymentFlow Flow,
    string ReturnPath,
    string FailPath,
    DateTimeOffset ExpiresAtUtc,
    bool IsLink = false);

/// <summary>
/// The status of an online payment as returned by the server-to-server status check of the payment gateway
/// (<c>getOrderStatusExtended.do</c>). Only the online payment flows build it from a gateway answer; never from browser input.
/// </summary>
/// <param name="State">State confirmed by the gateway.</param>
/// <param name="Amount">Pre-authorized amount (<see cref="GatewayPaymentState.PreAuthorized"/>) or debited amount (<see cref="GatewayPaymentState.Deposited"/>).</param>
/// <param name="GatewayOrderId">Order id given by the gateway, kept as the external reference.</param>
/// <param name="Detail">Gateway outcome detail (status code, action code) kept as the status reason, without card data.</param>
public sealed record ConfirmedGatewayStatus(
    GatewayPaymentState State,
    Money? Amount = null,
    string? GatewayOrderId = null,
    string? Detail = null);
