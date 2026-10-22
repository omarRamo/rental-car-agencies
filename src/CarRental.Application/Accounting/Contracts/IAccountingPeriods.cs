using CarRental.Domain.Common;

namespace CarRental.Application.Accounting.Contracts;

/// <summary>
/// The monthly closing hook (SPEC §8 "clôture mensuelle qui verrouille la période", ADR-0065): every write path that dates
/// a financial fact asks it before writing — the issue of an invoice or a credit note (Billing), a payment, a refund, a
/// capture, a deposit taken, released or retained (Payments), a cash session opened, a cash movement, a counted closing
/// (Cash), an expense recorded, changed or deleted — and gets <c>accounting.period.closed</c> for a day of a closed month.
/// </summary>
/// <remarks>
/// <para>No permission check (module contract): the caller checks its own.</para>
/// <para>Inside a unit of work it also takes the shared lock of the month (<c>sp_getapplock</c>, owned by the transaction):
/// a closing of the same month, which takes it exclusively, waits for the writers in progress and the writers that come
/// after it see the month closed. Call it before the first write of the transaction.</para>
/// </remarks>
public interface IAccountingPeriods
{
    /// <summary>Succeeds when the month of <paramref name="businessDate"/> (a Tunis calendar day) is open for the agency.</summary>
    Task<Result> EnsureOpenAsync(Guid agencyId, DateOnly businessDate, CancellationToken cancellationToken = default);

    /// <summary>The closed months of an agency, each as its first day, oldest first (screens show what can no longer change).</summary>
    Task<IReadOnlyList<DateOnly>> GetClosedMonthsAsync(Guid agencyId, CancellationToken cancellationToken = default);
}
