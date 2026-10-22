using CarRental.Domain.Common;

namespace CarRental.Application.Cash.Expenses;

/// <summary>
/// Application lock of the numbering of the expenses of an agency for a year (SQL Server <c>sp_getapplock</c>, exclusive,
/// owned by the current transaction, ADR-0063): expenses recorded at once take their numbers one after the other instead of
/// colliding on the counter row. Calling it outside <c>IUnitOfWork.ExecuteAsync</c> is a programming error.
/// </summary>
public interface IExpenseNumberLock
{
    /// <summary>Default time to wait for the expenses being numbered.</summary>
    static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Holds the numbering of <paramref name="agencyId"/> for <paramref name="year"/> until the transaction ends.</summary>
    Task<Result> AcquireAsync(Guid agencyId, int year, CancellationToken cancellationToken = default);
}
