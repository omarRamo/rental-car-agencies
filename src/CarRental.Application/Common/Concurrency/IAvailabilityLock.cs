using CarRental.Domain.Common;

namespace CarRental.Application.Common.Concurrency;

/// <summary>
/// Exclusive application lock on the availability of a vehicle category (ADR-0006): SQL Server
/// <c>sp_getapplock</c> on <c>availability:{categoryId}</c>, exclusive, owned by the current transaction.
/// </summary>
/// <remarks>
/// Every write that consumes capacity (create, extend, change a booking) must: open a unit of work, acquire this lock,
/// re-check availability, then write. The lock is released automatically at commit or rollback.
/// Calling it outside <see cref="Persistence.IUnitOfWork.ExecuteAsync(Func{CancellationToken, Task{Result}}, System.Data.IsolationLevel, CancellationToken)"/>
/// is a programming error and throws <see cref="InvalidOperationException"/>.
/// </remarks>
public interface IAvailabilityLock
{
    /// <summary>Default time to wait for a concurrent writer of the same category.</summary>
    static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Acquires the lock, waiting up to <paramref name="timeout"/> (<see cref="DefaultTimeout"/> when omitted).
    /// Returns <see cref="AvailabilityLockErrors.Timeout"/> when another transaction keeps it longer.
    /// </summary>
    Task<Result> AcquireAsync(Guid categoryId, TimeSpan? timeout = null, CancellationToken cancellationToken = default);
}
