namespace CarRental.Application.Payments.Contracts;

/// <summary>
/// Outbox message for the other modules (booking funnel, validation, "Ma réservation"): the status of an online
/// incoming payment changed, as confirmed by the gateway (invariant 7) — pre-authorized, captured, failed, cancelled
/// (released or expired) or refunded — whoever made the change (return of the browser, validation, catch-up job, back
/// office). Written in the transaction of the change. Delivered at least once and in no guaranteed order with other
/// messages: a handler must be idempotent and, when the order matters, read the current state with
/// <see cref="IOnlinePayments.FindAsync"/>. Subscribe with
/// <c>services.AddOutboxHandler&lt;OnlinePaymentStatusChanged, THandler&gt;()</c> (the domain events of the Payments
/// module are not part of its contract).
/// </summary>
/// <param name="PaymentId">The online payment (its order number is <c>PaymentId.ToString("N")</c>).</param>
/// <param name="BookingId">The booking it pays for.</param>
/// <param name="AgencyId">The agency the payment belongs to.</param>
/// <param name="Flow">Pre-authorization (submission) or one-step payment (balance, payment link).</param>
/// <param name="IsPaymentLink">A link handed to the customer (fallback mode, phone) rather than a page of the funnel.</param>
/// <param name="PreviousStatus">The status before the change.</param>
/// <param name="Status">The confirmed status.</param>
/// <param name="Expired">This change closes the payment as expired: the customer did not pay within the validity of the order.</param>
/// <param name="PaidLate">
/// This change records a payment made after the order had been closed as expired (the money is held or taken; the
/// booking must be checked, the payment is flagged for the back office).
/// </param>
public sealed record OnlinePaymentStatusChanged(
    Guid PaymentId,
    Guid BookingId,
    Guid AgencyId,
    OnlinePaymentFlowInfo Flow,
    bool IsPaymentLink,
    PaymentStatusInfo PreviousStatus,
    PaymentStatusInfo Status,
    bool Expired,
    bool PaidLate);
