using System.Globalization;
using CarRental.Application.Accounting.Contracts;
using CarRental.Application.Common.Persistence;
using CarRental.Domain.Accounting;
using CarRental.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace CarRental.Application.Accounting.Periods;

/// <summary>
/// Application lock of an accounting month of an agency (SQL Server <c>sp_getapplock</c> owned by the current transaction):
/// shared by the writers that date a financial fact in the month, exclusive for its closing and its reopening, so that no
/// fact slips into a month while it is being frozen. Calling it outside <c>IUnitOfWork.ExecuteAsync</c> is a programming
/// error.
/// </summary>
public interface IAccountingPeriodLock
{
    /// <summary>Default time to wait for a closing in progress.</summary>
    static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    /// <summary>The lock of a writer: many at once, none while the month is being closed.</summary>
    Task<Result> AcquireSharedAsync(string resource, CancellationToken cancellationToken = default);

    /// <summary>The lock of the closing or the reopening: alone, after the writers in progress.</summary>
    Task<Result> AcquireExclusiveAsync(string resource, CancellationToken cancellationToken = default);
}

/// <summary>Names of the accounting locks.</summary>
public static class AccountingLocks
{
    public static string Period(Guid agencyId, AccountingMonth month) =>
        string.Create(CultureInfo.InvariantCulture, $"accounting:period:{agencyId:N}:{month}");
}

/// <summary>
/// <see cref="IAccountingPeriods"/>: a month is closed when its period row says so; inside a unit of work the shared lock
/// of the month is taken first, so that a closing in progress is waited for.
/// </summary>
internal sealed class AccountingPeriods(IAppDbContext db, IUnitOfWork unitOfWork, IAccountingPeriodLock locks) : IAccountingPeriods
{
    public async Task<Result> EnsureOpenAsync(Guid agencyId, DateOnly businessDate, CancellationToken cancellationToken = default)
    {
        var month = AccountingMonth.Of(businessDate);
        if (unitOfWork.HasActiveTransaction)
        {
            var locked = await locks.AcquireSharedAsync(AccountingLocks.Period(agencyId, month), cancellationToken);
            if (locked.IsFailure)
            {
                return locked;
            }
        }

        var closed = await db.Set<AccountingPeriod>()
            .AsNoTracking()
            .AnyAsync(p => p.AgencyId == agencyId && p.Year == month.Year && p.Month == month.Month && p.Status == AccountingPeriodStatus.Closed, cancellationToken);
        return closed ? AccountingErrors.PeriodClosed : Result.Success();
    }

    public async Task<IReadOnlyList<DateOnly>> GetClosedMonthsAsync(Guid agencyId, CancellationToken cancellationToken = default)
    {
        var closed = await db.Set<AccountingPeriod>()
            .AsNoTracking()
            .Where(p => p.AgencyId == agencyId && p.Status == AccountingPeriodStatus.Closed)
            .Select(p => new { p.Year, p.Month })
            .ToListAsync(cancellationToken);
        return [.. closed.Select(p => new DateOnly(p.Year, p.Month, 1)).Order()];
    }
}
