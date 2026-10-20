using CarRental.Application.Cash.Contracts;
using CarRental.Application.Common.Persistence;
using CarRental.Domain.Cash;
using Microsoft.EntityFrameworkCore;

namespace CarRental.Application.Cash;

internal sealed class CashQueries(IAppDbContext db) : ICashQueries
{
    public async Task<IReadOnlyList<CashSessionTotalsInfo>> GetSessionTotalsAsync(
        DateOnly from,
        DateOnly to,
        Guid? agencyId = null,
        CancellationToken cancellationToken = default)
    {
        var query = db.Set<CashSession>().AsNoTracking().Where(s => s.BusinessDate >= from && s.BusinessDate <= to);
        if (agencyId is { } agency)
        {
            query = query.Where(s => s.AgencyId == agency);
        }

        var sessions = await query.ToListAsync(cancellationToken);
        return sessions
            .OrderBy(s => s.BusinessDate)
            .ThenBy(s => s.CashierName, StringComparer.Ordinal)
            .ThenBy(s => s.Id)
            .Select(s => new CashSessionTotalsInfo(
                s.Id,
                s.AgencyId,
                s.CashierId,
                s.CashierName,
                s.BusinessDate,
                s.Status.ToInfo(),
                s.OpeningFloat,
                CashMapping.TotalsOf(s.Movements),
                s.ExpectedRevenueCash,
                s.ExpectedDepositCash,
                s.CountedRevenueCash,
                s.CountedDepositCash,
                s.RevenueVariance,
                s.DepositVariance))
            .ToList();
    }
}
