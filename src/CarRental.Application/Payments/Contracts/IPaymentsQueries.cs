namespace CarRental.Application.Payments.Contracts;

/// <summary>
/// Read API of the Payments module for the other modules (no permission check: the calling module checks its own):
/// the money situation of a booking (balance at the departure, closing of the file) and the totals of the
/// reconciliations and accounting exports of Phase 3. Deposits never count as payments (invariant 8).
/// </summary>
public interface IPaymentsQueries
{
    /// <summary>Totals of the payments and of the deposits of a booking (zero when it has none).</summary>
    Task<BookingPaymentSummary> GetBookingSummaryAsync(Guid bookingId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Money received and paid back per Tunis day (the day it was captured or paid back) and means of payment, between
    /// <paramref name="from"/> and <paramref name="to"/> included, optionally for one agency; ordered by day then means.
    /// </summary>
    Task<IReadOnlyList<DailyPaymentTotal>> GetDailyTotalsByMethodAsync(
        DateOnly from,
        DateOnly to,
        Guid? agencyId = null,
        CancellationToken cancellationToken = default);

    /// <summary>Deposits still held, owed back to customers, in total and per mode.</summary>
    Task<DepositLiabilities> GetOpenDepositLiabilitiesAsync(Guid? agencyId = null, CancellationToken cancellationToken = default);
}
