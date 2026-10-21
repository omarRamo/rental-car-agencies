namespace CarRental.Domain.Payments;

/// <summary>Whether money comes from the customer or goes back to the customer.</summary>
public enum PaymentDirection
{
    /// <summary>Money received from the customer (encaissement).</summary>
    Incoming = 1,

    /// <summary>Money paid back to the customer, linked to the incoming payment it refunds.</summary>
    Refund = 2,
}

/// <summary>Means of payment (SPEC §8, "Paiements et cautions").</summary>
public enum PaymentMethod
{
    /// <summary>Espèces: always recorded in the open cash session of the agent who receives or pays back the money.</summary>
    Cash = 1,

    /// <summary>Payment card on the counter terminal (TPE).</summary>
    CardTerminal = 2,

    /// <summary>Chèque.</summary>
    Cheque = 3,

    /// <summary>Virement bancaire.</summary>
    BankTransfer = 4,

    /// <summary>Paid to a sales channel (e.g. Booking.com), which pays the agency later (reconciled in Phase 3).</summary>
    ChannelPrepaid = 5,

    /// <summary>
    /// Clic to Pay online payment (Phase 2). Its status changes only from a status confirmed server to server
    /// (<c>getOrderStatusExtended.do</c>, invariant 7), never from a counter operation.
    /// </summary>
    ClicToPay = 6,
}

/// <summary>Status of a payment (SPEC §8). For a refund, <see cref="Captured"/> means "paid back".</summary>
public enum PaymentStatus
{
    /// <summary>En attente: recorded, money not confirmed yet (bank transfer, cheque, online payment started).</summary>
    Pending = 1,

    /// <summary>Pré-autorisé: amount blocked on the card, not captured yet (Clic to Pay only).</summary>
    PreAuthorized = 2,

    /// <summary>Encaissé: the money was received (for a refund: paid back to the customer).</summary>
    Captured = 3,

    /// <summary>Annulé: cancelled before capture (pending payment cancelled, pre-authorization reversed).</summary>
    Cancelled = 4,

    /// <summary>Remboursé: an incoming payment whose whole captured amount was paid back.</summary>
    Refunded = 5,

    /// <summary>Échoué: declined by the payment gateway.</summary>
    Failed = 6,
}

/// <summary>How a security deposit (caution) is held (SPEC §6 "Départ" and §8).</summary>
public enum DepositMode
{
    /// <summary>Card imprint on the counter terminal (empreinte TPE): nothing is debited unless retained.</summary>
    CardImprint = 1,

    /// <summary>Cash, kept apart from the revenue cash of the session (SPEC §8 "Caisse").</summary>
    Cash = 2,

    /// <summary>Cheque kept by the agency until the return.</summary>
    Cheque = 3,
}

/// <summary>Status of a security deposit: a liability to the customer while <see cref="Held"/>.</summary>
public enum DepositStatus
{
    /// <summary>Bloquée: the agency holds it; it is owed back to the customer.</summary>
    Held = 1,

    /// <summary>Restituée: returned in full.</summary>
    Released = 2,

    /// <summary>Retenue partielle: part retained with a justification, the rest returned.</summary>
    PartiallyRetained = 3,

    /// <summary>Retenue: retained in full with a justification.</summary>
    Retained = 4,
}

/// <summary>
/// State of an online payment as confirmed by the payment gateway, server to server (Phase 2 maps the Clic to Pay codes
/// to these values; TODO(spec): codes other than 0, 1 and 2 come from the SMT documentation).
/// </summary>
public enum GatewayPaymentState
{
    /// <summary>Order registered, not paid yet (Clic to Pay status 0).</summary>
    Registered = 1,

    /// <summary>Amount pre-authorized (Clic to Pay status 1).</summary>
    PreAuthorized = 2,

    /// <summary>Amount debited (Clic to Pay status 2, after <c>deposit.do</c> or a one-step payment).</summary>
    Deposited = 3,

    /// <summary>Pre-authorization reversed (<c>reverse.do</c>) or expired.</summary>
    Reversed = 4,

    /// <summary>Payment declined.</summary>
    Declined = 5,

    /// <summary>
    /// The gateway confirms the order is still unpaid (status 0) once its validity is over, or it never handed a payment
    /// page to anyone (registration not confirmed): the payment is closed as expired. A later payment of the same order
    /// is still recorded (late payment, flagged).
    /// </summary>
    Expired = 6,
}

/// <summary>How an online payment takes the money (SPEC §9).</summary>
public enum OnlinePaymentFlow
{
    /// <summary>Two steps: <c>registerPreAuth.do</c> blocks the amount, <c>deposit.do</c> captures it (or less) at validation.</summary>
    PreAuthorization = 1,

    /// <summary>One step: <c>register.do</c>, captured as soon as the customer pays (balance, payment link, fallback mode).</summary>
    OneStep = 2,
}

/// <summary>A gateway operation requested by the platform and not confirmed yet by a status read.</summary>
public enum PendingGatewayOperation
{
    /// <summary><c>deposit.do</c> on a pre-authorized payment (amount in <see cref="Payment.PendingGatewayAmount"/>).</summary>
    Capture = 1,

    /// <summary><c>reverse.do</c> on a pre-authorized payment, or a release asked before the customer paid.</summary>
    Reverse = 2,

    /// <summary><c>refund.do</c> sent for a refund (set on the refund).</summary>
    Refund = 3,
}

/// <summary>Why an online payment needs a look from the back office (mismatch with the gateway, unknown status…).</summary>
public enum GatewayAttention
{
    /// <summary>The gateway reports a status code the platform does not know yet (TODO(spec): SMT codes).</summary>
    UnknownStatus = 1,

    /// <summary>The status of the gateway contradicts the recorded status (money held or taken while cancelled…).</summary>
    StatusMismatch = 2,

    /// <summary>The amount confirmed by the gateway differs from the recorded or requested amount.</summary>
    AmountMismatch = 3,

    /// <summary>The customer paid after the order expired: the money is recorded, the booking must be checked.</summary>
    LatePayment = 4,

    /// <summary>A capture was requested but the gateway does not show it: the validation must be retried.</summary>
    CaptureNotConfirmed = 5,

    /// <summary>A reversal was requested but the gateway does not show it yet.</summary>
    ReversalNotConfirmed = 6,

    /// <summary>A refund was sent but its outcome is not confirmed: check it in the Clic to Pay merchant portal.</summary>
    RefundNotConfirmed = 7,

    /// <summary>The gateway refused a capture, a reversal or a refund (error code in the call log).</summary>
    OperationRejected = 8,
}

/// <summary>An operation of the Clic to Pay REST API (SPEC §9).</summary>
public enum GatewayOperation
{
    /// <summary><c>register.do</c>: one-step payment.</summary>
    Register = 1,

    /// <summary><c>registerPreAuth.do</c>: pre-authorization.</summary>
    RegisterPreAuth = 2,

    /// <summary><c>deposit.do</c>: capture of a pre-authorization.</summary>
    Deposit = 3,

    /// <summary><c>reverse.do</c>: cancellation of a pre-authorization.</summary>
    Reverse = 4,

    /// <summary><c>refund.do</c>: refund of a captured payment.</summary>
    Refund = 5,

    /// <summary><c>getOrderStatusExtended.do</c>: server-to-server status, the only source of truth.</summary>
    GetOrderStatusExtended = 6,
}

/// <summary>Outcome of a call to the payment gateway, as written in the call log.</summary>
public enum GatewayCallOutcome
{
    /// <summary>The gateway answered and accepted the request.</summary>
    Succeeded = 1,

    /// <summary>The gateway answered with an error code.</summary>
    GatewayError = 2,

    /// <summary>No usable answer: network failure, time-out, HTTP 5xx. The request may or may not have been processed.</summary>
    Unavailable = 3,

    /// <summary>The answer could not be understood (unexpected HTTP status, malformed or incomplete body).</summary>
    InvalidResponse = 4,
}

/// <summary>Rules attached to the means of payment.</summary>
public static class PaymentMethods
{
    /// <summary>Methods an agent records at the counter (every method but the online one).</summary>
    public static bool IsCounter(PaymentMethod method) => method is PaymentMethod.Cash or PaymentMethod.CardTerminal
        or PaymentMethod.Cheque or PaymentMethod.BankTransfer or PaymentMethod.ChannelPrepaid;

    /// <summary>
    /// Methods whose external reference is mandatory, for the reconciliations: cheque number, transfer reference, channel
    /// booking reference. Optional for cash and the card terminal (the terminal batch is reconciled by day).
    /// </summary>
    public static bool RequiresReference(PaymentMethod method) =>
        method is PaymentMethod.Cheque or PaymentMethod.BankTransfer or PaymentMethod.ChannelPrepaid;

    /// <summary>Methods that can be recorded before the money is confirmed (pending): bank transfer and cheque.</summary>
    public static bool CanAwaitConfirmation(PaymentMethod method) => method is PaymentMethod.BankTransfer or PaymentMethod.Cheque;

    /// <summary>Methods an agent can use to pay money back at the counter.</summary>
    public static bool CanRefund(PaymentMethod method) =>
        method is PaymentMethod.Cash or PaymentMethod.CardTerminal or PaymentMethod.Cheque or PaymentMethod.BankTransfer;
}
