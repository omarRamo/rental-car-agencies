using System.Data;
using CarRental.Domain.Common;

namespace CarRental.Application.Common.Persistence;

/// <summary>
/// Runs an operation in a database transaction. Pending changes are saved, then the transaction commits when the
/// operation returns a success; a failed result (or an exception) rolls it back and clears the change tracker.
/// </summary>
/// <remarks>
/// <para>The whole operation is retried on transient SQL failures (Azure SQL resiliency), so it must be
/// self-contained: load what it needs inside the delegate and have no side effect outside the database
/// (use the outbox for e-mails and calls to partners).</para>
/// <para>Required by <c>IAvailabilityLock</c> and <c>IDocumentNumberGenerator</c>, which work at transaction scope.</para>
/// </remarks>
public interface IUnitOfWork
{
    /// <summary><see langword="true"/> while an operation of this unit of work is running in a transaction.</summary>
    bool HasActiveTransaction { get; }

    Task<Result> ExecuteAsync(
        Func<CancellationToken, Task<Result>> operation,
        IsolationLevel isolationLevel = IsolationLevel.ReadCommitted,
        CancellationToken cancellationToken = default);

    Task<Result<T>> ExecuteAsync<T>(
        Func<CancellationToken, Task<Result<T>>> operation,
        IsolationLevel isolationLevel = IsolationLevel.ReadCommitted,
        CancellationToken cancellationToken = default);
}
