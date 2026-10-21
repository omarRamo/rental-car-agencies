namespace CarRental.Application.Payments.Contracts;

/// <summary>
/// Read API of the Payments module for the Billing module (package 3A): the payments of a file listed on its invoice and
/// the totals of many files at once (sales journal, open amounts of the customers). Deposits never count as payments
/// (invariant 8): their totals come apart. No permission check (the caller checks its own).
/// </summary>
/// <remarks>A separate interface from <see cref="IPaymentsQueries"/>, so that its implementers are not affected.</remarks>
public interface IPaymentsBillingQueries
{
    /// <summary>The payments and refunds of a booking, oldest first, their totals and the totals of its deposits.</summary>
    Task<BookingBillingPayments> GetBookingPaymentsAsync(Guid bookingId, CancellationToken cancellationToken = default);

    /// <summary>The payment and deposit totals of each of <paramref name="bookingIds"/> (zero for a booking without any).</summary>
    Task<IReadOnlyDictionary<Guid, BookingPaymentSummary>> GetSummariesAsync(
        IReadOnlyCollection<Guid> bookingIds,
        CancellationToken cancellationToken = default);
}

/// <summary>The money of a booking for its invoice: payments and refunds (oldest first) with their totals, and the deposits.</summary>
public sealed record BookingBillingPayments(Guid BookingId, IReadOnlyList<PaymentInfo> Payments, PaymentTotals Totals, DepositTotals Deposits);
