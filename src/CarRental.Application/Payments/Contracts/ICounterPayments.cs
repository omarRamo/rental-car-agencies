using CarRental.Domain.Common;

namespace CarRental.Application.Payments.Contracts;

/// <summary>
/// Payments received and paid back at the counter for a booking (back office, departure and return). Writes need
/// <c>payments.manage</c>, reads <c>payments.read</c>. Cash goes into the open cash session of the acting user, in the
/// same transaction: without an open session (or with a session of another agency) the operation fails.
/// </summary>
/// <remarks>
/// A method can run inside the unit of work of the caller (e.g. the departure of a booking): it then joins its
/// transaction, and a failed result must fail the caller's unit of work too (its tracked changes are not undone).
/// Clic to Pay payments are not recorded here: their status comes from the payment gateway only (invariant 7).
/// </remarks>
public interface ICounterPayments
{
    /// <summary>Records money received for a booking (captured at once, or pending for a transfer or a cheque awaiting confirmation). Returns the payment id.</summary>
    Task<Result<Guid>> RecordPaymentAsync(CounterPaymentRequest request, CancellationToken cancellationToken = default);

    /// <summary>Pays money back on a captured payment, never more than what remains refundable. Returns the refund id.</summary>
    Task<Result<Guid>> RecordRefundAsync(CounterRefundRequest request, CancellationToken cancellationToken = default);

    /// <summary>Confirms a pending transfer or cheque (the money is there); the reference can be completed.</summary>
    Task<Result> ConfirmPendingAsync(Guid paymentId, string? externalReference = null, CancellationToken cancellationToken = default);

    /// <summary>Cancels a pending transfer or cheque, with a mandatory reason.</summary>
    Task<Result> CancelPendingAsync(Guid paymentId, string reason, CancellationToken cancellationToken = default);

    /// <summary>The payments and refunds of a booking, oldest first, with their totals (captured, refunded, net paid).</summary>
    Task<Result<BookingPayments>> GetBookingPaymentsAsync(Guid bookingId, CancellationToken cancellationToken = default);
}
