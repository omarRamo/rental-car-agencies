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
