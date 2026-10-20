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
/// failed only through <see cref="ApplyConfirmedGatewayStatus"/>, which Phase 2 feeds exclusively from the server-to-server
/// status check (<c>getOrderStatusExtended.do</c>); every counter operation refuses it. The id of the payment, 32
/// hexadecimal characters, is meant to be the Clic to Pay order number, which makes the processing idempotent.</para>
/// <para>Refunds never exceed what was captured: <see cref="RefundableAmount"/> = captured − refunded. A payment whose
/// whole captured amount was paid back becomes <see cref="PaymentStatus.Refunded"/>.</para>
/// </remarks>
public sealed class Payment : AggregateRoot
{
    public const int ReferenceMaxLength = 100;
    public const int NotesMaxLength = 500;
    public const int ReasonMinLength = 3;
    public const int ReasonMaxLength = 500;

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

    public bool IsOnline => Method == PaymentMethod.ClicToPay;

    /// <summary>True when the money moved: captured, or captured then fully refunded.</summary>
    public bool IsSettled => Status is PaymentStatus.Captured or PaymentStatus.Refunded;

    /// <summary>What can still be paid back on this incoming payment: captured minus refunded.</summary>
    public Money RefundableAmount =>
        Direction == PaymentDirection.Incoming && Status == PaymentStatus.Captured ? CapturedAmount - RefundedAmount : Money.Zero;

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
    /// Starts an online Clic to Pay payment (Phase 2): pending until the payment gateway confirms its status server to
    /// server (<see cref="ApplyConfirmedGatewayStatus"/>).
    /// </summary>
    public static Result<Payment> StartOnline(Guid agencyId, Guid bookingId, Money amount, AuditActor by, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(by);
        if (agencyId == Guid.Empty)
        {
            return PaymentsErrors.AgencyRequired;
        }

        if (bookingId == Guid.Empty)
        {
            return PaymentsErrors.BookingRequired;
        }

        if (!PaymentText.IsValidAmount(amount, MaxAmount))
        {
            return PaymentsErrors.AmountInvalid;
        }

        var payment = new Payment(NewId(), agencyId, bookingId, PaymentDirection.Incoming, PaymentMethod.ClicToPay, amount, by, at);
        payment.Raise(new PaymentRecorded(payment.Id, bookingId, agencyId, payment.Direction, payment.Method, payment.Status));
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
    /// Applies the status of this Clic to Pay payment as confirmed by the payment gateway, server to server. This is the
    /// only way an online payment becomes pre-authorized, captured, cancelled or failed (invariant 7): never call it
    /// with what the browser of the customer reports (return URL parameters prove nothing). Replaying the current status
    /// is accepted (idempotent processing of the same order).
    /// </summary>
    public Result ApplyConfirmedGatewayStatus(ConfirmedGatewayStatus confirmed, AuditActor by, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(confirmed);
        ArgumentNullException.ThrowIfNull(by);
        if (!IsOnline || Direction != PaymentDirection.Incoming)
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
            GatewayPaymentState.Registered => Status == PaymentStatus.Pending ? Result.Success() : PaymentsErrors.GatewayTransitionInvalid,
            GatewayPaymentState.PreAuthorized => PreAuthorize(confirmed, by, at),
            GatewayPaymentState.Deposited => CaptureOnline(confirmed, by, at),
            GatewayPaymentState.Reversed => Reverse(confirmed, by, at),
            GatewayPaymentState.Declined => Decline(confirmed, by, at),
            _ => PaymentsErrors.GatewayTransitionInvalid,
        };

        if (outcome.IsSuccess && orderId is not null)
        {
            ExternalReference ??= orderId;
        }

        return outcome;
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

    private Result PreAuthorize(ConfirmedGatewayStatus confirmed, AuditActor by, DateTimeOffset at)
    {
        if (Status == PaymentStatus.PreAuthorized)
        {
            return Result.Success();
        }

        if (Status != PaymentStatus.Pending)
        {
            return PaymentsErrors.GatewayTransitionInvalid;
        }

        if (confirmed.Amount != Amount)
        {
            return PaymentsErrors.GatewayAmountInvalid;
        }

        TransitionTo(PaymentStatus.PreAuthorized, by, at, GatewayDetail(confirmed));
        return Result.Success();
    }

    private Result CaptureOnline(ConfirmedGatewayStatus confirmed, AuditActor by, DateTimeOffset at)
    {
        if (Status == PaymentStatus.Captured)
        {
            return confirmed.Amount == CapturedAmount ? Result.Success() : PaymentsErrors.GatewayAmountInvalid;
        }

        if (Status is not (PaymentStatus.Pending or PaymentStatus.PreAuthorized))
        {
            return PaymentsErrors.GatewayTransitionInvalid;
        }

        if (confirmed.Amount is not { } deposited || !PaymentText.IsValidAmount(deposited, MaxAmount) || deposited > Amount)
        {
            return PaymentsErrors.GatewayAmountInvalid;
        }

        Capture(deposited, at);
        TransitionTo(PaymentStatus.Captured, by, at, GatewayDetail(confirmed));
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

        TransitionTo(PaymentStatus.Cancelled, by, at, GatewayDetail(confirmed));
        return Result.Success();
    }

    private Result Decline(ConfirmedGatewayStatus confirmed, AuditActor by, DateTimeOffset at)
    {
        if (Status == PaymentStatus.Failed)
        {
            return Result.Success();
        }

        if (Status != PaymentStatus.Pending)
        {
            return PaymentsErrors.GatewayTransitionInvalid;
        }

        TransitionTo(PaymentStatus.Failed, by, at, GatewayDetail(confirmed));
        return Result.Success();
    }

    private static string? GatewayDetail(ConfirmedGatewayStatus confirmed)
    {
        var detail = PaymentText.Clean(confirmed.Detail);
        return detail is { Length: > ReasonMaxLength } ? detail[..ReasonMaxLength] : detail;
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

/// <summary>
/// The status of an online payment as returned by the server-to-server status check of the payment gateway
/// (<c>getOrderStatusExtended.do</c>). Only the Phase 2 gateway client builds it; never from browser input.
/// </summary>
/// <param name="State">State confirmed by the gateway.</param>
/// <param name="Amount">Pre-authorized amount (<see cref="GatewayPaymentState.PreAuthorized"/>) or debited amount (<see cref="GatewayPaymentState.Deposited"/>).</param>
/// <param name="GatewayOrderId">Order id given by the gateway, kept as the external reference.</param>
/// <param name="Detail">Gateway outcome detail (action code, message) kept as the status reason, without card data.</param>
public sealed record ConfirmedGatewayStatus(
    GatewayPaymentState State,
    Money? Amount = null,
    string? GatewayOrderId = null,
    string? Detail = null);
