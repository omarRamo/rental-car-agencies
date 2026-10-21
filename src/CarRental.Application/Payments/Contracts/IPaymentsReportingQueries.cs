namespace CarRental.Application.Payments.Contracts;

/// <summary>
/// Read API of the Payments module for the Reporting module (unpaid amounts, ADR-0046): the payment totals of many
/// bookings in one call. Deposits never count (invariant 8). No permission check (the caller checks its own).
/// </summary>
/// <remarks>A separate interface from <see cref="IPaymentsQueries"/>, so that its implementers are not affected.</remarks>
public interface IPaymentsReportingQueries
{
    /// <summary>
    /// The totals of the payments of each of <paramref name="bookingIds"/> that has at least one payment (captured,
    /// refunded, net paid, pending, pre-authorized); a booking without payment is absent.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, PaymentTotals>> GetPaymentTotalsByBookingAsync(
        IReadOnlyCollection<Guid> bookingIds,
        CancellationToken cancellationToken = default);
}
