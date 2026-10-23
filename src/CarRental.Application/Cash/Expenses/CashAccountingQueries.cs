using CarRental.Application.Cash.Contracts;
using CarRental.Application.Common.Persistence;
using CarRental.Domain.Cash;
using CarRental.Domain.Cash.Expenses;
using Microsoft.EntityFrameworkCore;

namespace CarRental.Application.Cash.Expenses;

/// <summary><see cref="ICashAccountingQueries"/>: no-tracking projections of the expenses and the cash movements of a period.</summary>
internal sealed class CashAccountingQueries(IAppDbContext db) : ICashAccountingQueries
{
    public async Task<IReadOnlyList<ExpenseEntryInfo>> GetExpensesAsync(Guid agencyId, DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
    {
        if (to < from)
        {
            return [];
        }

        var expenses = await db.Set<Expense>()
            .AsNoTracking()
            .Where(e => e.AgencyId == agencyId && e.Date >= from && e.Date <= to && !e.IsDeleted)
            .ToListAsync(cancellationToken);
        return [.. expenses.OrderBy(e => e.Date).ThenBy(e => e.Number, StringComparer.Ordinal).Select(e => e.ToEntry())];
    }

    public async Task<IReadOnlyList<CashMovementEntryInfo>> GetMovementsAsync(Guid agencyId, DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
    {
        if (to < from)
        {
            return [];
        }

        var sessions = await db.Set<CashSession>()
            .AsNoTracking()
            .Where(s => s.AgencyId == agencyId && s.BusinessDate >= from && s.BusinessDate <= to)
            .ToListAsync(cancellationToken);
        var expenseIds = sessions.SelectMany(s => s.Movements).Where(m => m.ExpenseId is not null).Select(m => m.ExpenseId!.Value).Distinct().ToList();
        var numbers = expenseIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await db.Set<Expense>()
                .AsNoTracking()
                .IgnoreQueryFilters([QueryFilters.SoftDelete])
                .Where(e => expenseIds.Contains(e.Id))
                .ToDictionaryAsync(e => e.Id, e => e.Number, cancellationToken);
        return
        [
            .. sessions
                .OrderBy(s => s.BusinessDate)
                .ThenBy(s => s.OpenedAtUtc)
                .ThenBy(s => s.Id)
                .SelectMany(s => s.Movements.OrderBy(m => m.Sequence).Select(m => new CashMovementEntryInfo(
                    m.Id,
                    s.Id,
                    s.BusinessDate,
                    s.CashierName,
                    m.Sequence,
                    m.Kind.ToInfo(),
                    m.Amount,
                    m.RevenueEffect,
                    m.DepositEffect,
                    m.Reason,
                    m.BookingId,
                    m.PaymentId,
                    m.DepositId,
                    m.ExpenseId,
                    m.ExpenseId is { } expenseId ? numbers.GetValueOrDefault(expenseId) : null,
                    m.RelatedSessionId,
                    m.RecordedAtUtc))),
        ];
    }
}
