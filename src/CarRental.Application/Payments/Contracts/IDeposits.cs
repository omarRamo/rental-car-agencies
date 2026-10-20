using CarRental.Domain.Common;

namespace CarRental.Application.Payments.Contracts;

/// <summary>
/// Security deposits (cautions) of the bookings: taken at the departure, released or retained at the return, exactly
/// once. A deposit is a liability to the customer, never revenue (invariant 8). Writes need <c>payments.manage</c>,
/// reads <c>payments.read</c>. A cash deposit is received and given back through the open cash session of the acting
/// user (deposit cash, counted apart from the revenue cash).
/// </summary>
/// <remarks>
/// Like <see cref="ICounterPayments"/>, a method can run inside the unit of work of the caller; a failed result must
/// then fail the caller's unit of work too.
/// </remarks>
public interface IDeposits
{
    /// <summary>Takes a deposit for a booking. Returns the deposit id.</summary>
    Task<Result<Guid>> TakeAsync(DepositRequest request, CancellationToken cancellationToken = default);

    /// <summary>Returns the whole deposit to the customer.</summary>
    Task<Result> ReleaseAsync(Guid depositId, CancellationToken cancellationToken = default);

    /// <summary>Keeps part (or all) of the deposit with a mandatory justification; the rest is returned.</summary>
    Task<Result> RetainAsync(Guid depositId, DepositRetentionRequest request, CancellationToken cancellationToken = default);

    /// <summary>The deposits of a booking, oldest first, with what is still owed back (open liability).</summary>
    Task<Result<BookingDeposits>> GetBookingDepositsAsync(Guid bookingId, CancellationToken cancellationToken = default);
}
