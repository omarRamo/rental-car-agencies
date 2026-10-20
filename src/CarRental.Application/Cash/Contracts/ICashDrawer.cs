using CarRental.Domain.Common;

namespace CarRental.Application.Cash.Contracts;

/// <summary>
/// The cash drawer of the acting user, used by the Payments module to put cash payments, refunds and deposits into the
/// open cash session in the same transaction as the payment (other modules go through the Payments contracts).
/// Requires <c>cash.own</c>: cash always goes through the user's own open session.
/// </summary>
/// <remarks>
/// The <c>Record…</c> methods must run inside the caller's unit of work (<c>IUnitOfWork.ExecuteAsync</c>); they throw
/// <see cref="InvalidOperationException"/> otherwise. They add the movement to the tracked session and leave the save
/// to the unit of work: a failure must fail it.
/// </remarks>
public interface ICashDrawer
{
    /// <summary>The open session of the acting user, which must belong to <paramref name="agencyId"/>.</summary>
    Task<Result<Guid>> GetOpenSessionIdAsync(Guid agencyId, CancellationToken cancellationToken = default);

    /// <summary>A cash payment received (revenue cash +).</summary>
    Task<Result> RecordPaymentReceivedAsync(CashPaymentEntry entry, CancellationToken cancellationToken = default);

    /// <summary>A cash refund paid (revenue cash −); fails when the drawer does not hold enough revenue cash.</summary>
    Task<Result> RecordRefundPaidAsync(CashPaymentEntry entry, CancellationToken cancellationToken = default);

    /// <summary>A cash deposit received (deposit cash +).</summary>
    Task<Result> RecordDepositReceivedAsync(CashDepositEntry entry, CancellationToken cancellationToken = default);

    /// <summary>
    /// A cash deposit returned and/or retained (the retained part becomes revenue cash). A deposit received in another
    /// session is first brought into this drawer, and leaves the drawer that received it when that session is still open.
    /// </summary>
    Task<Result> RecordDepositSettledAsync(CashDepositSettlementEntry entry, CancellationToken cancellationToken = default);
}
