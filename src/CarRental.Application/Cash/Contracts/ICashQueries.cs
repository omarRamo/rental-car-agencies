namespace CarRental.Application.Cash.Contracts;

/// <summary>
/// Read API of the Cash module for the other modules (no permission check: the calling module checks its own), for the
/// reconciliation of the cash with the payments (Phase 3, "caisse contre encaissements").
/// </summary>
public interface ICashQueries
{
    /// <summary>
    /// Sessions whose business day lies between <paramref name="from"/> and <paramref name="to"/> (included), optionally
    /// of one agency, with the totals of their movements; ordered by day, then cashier.
    /// </summary>
    Task<IReadOnlyList<CashSessionTotalsInfo>> GetSessionTotalsAsync(
        DateOnly from,
        DateOnly to,
        Guid? agencyId = null,
        CancellationToken cancellationToken = default);
}
