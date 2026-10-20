using CarRental.Application.Common.Persistence;
using CarRental.Domain.Cash;
using CarRental.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace CarRental.Application.Cash;

/// <summary>Data access helpers of the Cash module.</summary>
internal static class CashPersistence
{
    /// <summary>The whole session with its movements (auto-included), tracked.</summary>
    public static Task<CashSession?> LoadSessionAsync(this IAppDbContext db, Guid sessionId, CancellationToken cancellationToken) =>
        db.Set<CashSession>().FirstOrDefaultAsync(s => s.Id == sessionId, cancellationToken);

    /// <summary>The sessions of a cashier that matter to open a new one: the open one, and those of the agency that day.</summary>
    public static async Task<List<ExistingCashSession>> ExistingSessionsAsync(
        this IAppDbContext db,
        Guid cashierId,
        Guid agencyId,
        DateOnly businessDate,
        CancellationToken cancellationToken) =>
        await db.Set<CashSession>()
            .AsNoTracking()
            .Where(s => s.CashierId == cashierId
                        && (s.Status == CashSessionStatus.Open || (s.AgencyId == agencyId && s.BusinessDate == businessDate)))
            .Select(s => new ExistingCashSession(s.AgencyId, s.BusinessDate, s.Status))
            .ToListAsync(cancellationToken);

    /// <summary>Runs the operation in a unit of work; a concurrent change of the same session becomes a business conflict.</summary>
    public static async Task<Result<T>> ExecuteGuardedAsync<T>(
        this IUnitOfWork unitOfWork,
        Func<CancellationToken, Task<Result<T>>> operation,
        CancellationToken cancellationToken)
    {
        try
        {
            return await unitOfWork.ExecuteAsync(operation, cancellationToken: cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return CashErrors.ConcurrentUpdate;
        }
    }
}
