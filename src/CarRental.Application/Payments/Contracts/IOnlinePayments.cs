using CarRental.Domain.Common;

namespace CarRental.Application.Payments.Contracts;

/// <summary>
/// Online payments with Clic to Pay (SPEC §9, ADR-0038, ADR-0039), for the other modules (booking funnel, validation,
/// "Ma réservation") and the back office. A payment is pre-authorized, captured or cancelled only from the status read
/// server to server (<c>getOrderStatusExtended.do</c>), never from what the browser reports (invariant 7); every
/// operation is idempotent by order number (the payment id) and reads the status first when a previous call ended
/// without a clear answer.
/// </summary>
/// <remarks>
/// <para>Permissions: none here except for the approval of refunds — the calling module checks its own (the funnel its
/// booking token, the validation <c>requests.manage</c>); the back office goes through <c>OnlinePaymentAdministration</c>.</para>
/// <para>Never call these methods inside a unit of work (<c>IUnitOfWork.ExecuteAsync</c>): they call the payment
/// gateway, which must not hold a database transaction (or a capacity lock) open; they throw
/// <see cref="InvalidOperationException"/> otherwise. Commit the booking first, then call them.</para>
/// </remarks>
public interface IOnlinePayments
{
    /// <summary>
    /// Whether online payment can be offered now (<c>ClicToPay:Enabled</c>): otherwise every operation fails with
    /// <c>payments.gateway.disabled</c> and the booking funnel submits without pre-authorization (payment link mode).
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Web submission: creates the pending online payment of the guaranteed amount, registers the pre-authorization
    /// (<c>registerPreAuth.do</c>, order number = payment id) and returns the address of the payment page to send the
    /// customer to. A second call for the same booking, amount and paths within ten minutes returns the same page
    /// (double submission).
    /// </summary>
    Task<Result<OnlinePaymentRedirect>> StartPreAuthorizationAsync(OnlinePaymentRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// One-step payment (<c>register.do</c>): balance in "Ma réservation", payment by link (phone, fallback mode of the
    /// validation), valid <see cref="OnlinePaymentRequest.Validity"/> (by default <c>payments.payment_link_validity</c>,
    /// 24 h, for a link; <c>payments.online_attempt_validity</c>, 1 h, otherwise).
    /// </summary>
    Task<Result<OnlinePaymentRedirect>> StartPaymentAsync(OnlinePaymentRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// The browser came back from the payment page (return or failure address): identifies the order from the query (our
    /// order number, else the gateway order id), reads its status server to server, applies it, and returns the local path
    /// to redirect to (the return path when pre-authorized or captured, the failure path when failed or cancelled; while
    /// unconfirmed, the path of the address the browser came back to). The query values prove nothing.
    /// </summary>
    Task<Result<OnlinePaymentReturn>> HandleReturnAsync(OnlinePaymentReturnRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Validation: captures the pre-authorization (<c>deposit.do</c>, at most the pre-authorized amount; all of it when
    /// <paramref name="amount"/> is null), then reads the status: success only once the gateway confirms the deposit.
    /// Capturing an already captured payment returns it unchanged. After a capture left without answer, the status is
    /// read first: the capture is sent again only when the read shows it had no effect (two minutes later at the
    /// earliest), and the error of the read is returned when it fails (nothing is sent on a stale state);
    /// <c>payments.online.capture_unconfirmed</c> means the gateway acknowledged it and the catch-up job confirms it.
    /// </summary>
    Task<Result<OnlinePaymentInfo>> CaptureAsync(Guid paymentId, decimal? amount = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// <see cref="CaptureAsync"/> on the most recent pre-authorized payment of the booking (or returns the captured one
    /// when it is already captured); <c>payments.online.not_pre_authorized</c> when there is none.
    /// </summary>
    Task<Result<OnlinePaymentInfo>> CaptureBookingAsync(Guid bookingId, decimal? amount = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Refusal or expiry: reverses the pre-authorization (<c>reverse.do</c>) then reads the status. Before the customer
    /// paid, the release is recorded and performed as soon as the gateway confirms a pre-authorization (catch-up job); an
    /// unpaid order otherwise expires. Reversing a cancelled or failed payment returns it unchanged. When the status that
    /// must be read first cannot be read, the release of a payment not made yet is still recorded; for any other, the
    /// error of the read is returned and nothing is sent.
    /// </summary>
    Task<Result<OnlinePaymentInfo>> ReverseAsync(Guid paymentId, CancellationToken cancellationToken = default);

    /// <summary>
    /// <see cref="ReverseAsync"/> on every pending or pre-authorized online payment of the booking (for an idempotent
    /// outbox handler on the refusal, expiry or cancellation of the booking). Releases that cannot be confirmed now are
    /// completed by the catch-up job; the returned list shows each payment after the call.
    /// </summary>
    Task<Result<IReadOnlyList<OnlinePaymentInfo>>> ReverseBookingAsync(Guid bookingId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Refunds part or all of a captured online payment (<c>refund.do</c>), with a reason. The amount is reserved until
    /// the gateway confirms it. Above <c>payments.online_refund_approval_threshold</c> (refunds of the payment added up)
    /// it waits for the approval of a manager, unless the acting user holds <c>payments.refunds.approve</c>.
    /// </summary>
    Task<Result<OnlineRefundOutcome>> RefundAsync(OnlineRefundRequest request, CancellationToken cancellationToken = default);

    /// <summary>A manager (<c>payments.refunds.approve</c>) approves a refund waiting for approval; it is sent to the gateway.</summary>
    Task<Result<OnlineRefundOutcome>> ApproveRefundAsync(Guid refundId, CancellationToken cancellationToken = default);

    /// <summary>A manager (<c>payments.refunds.approve</c>) rejects a refund waiting for approval, with a reason; the amount is released.</summary>
    Task<Result> RejectRefundAsync(Guid refundId, string reason, CancellationToken cancellationToken = default);

    /// <summary>Reads the status of the payment server to server and applies it (back office "re-check", catch-up).</summary>
    Task<Result<OnlinePaymentInfo>> RefreshStatusAsync(Guid paymentId, CancellationToken cancellationToken = default);

    /// <summary>An online payment, or <see langword="null"/> when there is none with this id.</summary>
    Task<OnlinePaymentInfo?> FindAsync(Guid paymentId, CancellationToken cancellationToken = default);

    /// <summary>The online payments of a booking, oldest first, and their refunds.</summary>
    Task<IReadOnlyList<OnlinePaymentInfo>> GetBookingOnlinePaymentsAsync(Guid bookingId, CancellationToken cancellationToken = default);
}
