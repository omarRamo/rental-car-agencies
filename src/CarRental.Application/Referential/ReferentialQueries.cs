using CarRental.Application.Common.Persistence;
using CarRental.Application.Referential.Contracts;
using CarRental.Domain.Referential;
using Microsoft.EntityFrameworkCore;

namespace CarRental.Application.Referential;

internal sealed class ReferentialQueries(IAppDbContext db) : IReferentialQueries
{
    public async Task<IReadOnlyList<AgencyInfo>> GetAgenciesAsync(CancellationToken cancellationToken = default) =>
        await db.Set<Agency>()
            .AsNoTracking()
            .OrderBy(a => a.Name)
            .Select(a => new AgencyInfo(a.Id, a.Name, a.City, a.Phone, a.Email, a.IsActive))
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<LocationInfo>> GetLocationsAsync(Guid? agencyId = null, CancellationToken cancellationToken = default)
    {
        var query = db.Set<Location>().AsNoTracking().Where(l => l.IsActive);
        if (agencyId is { } id)
        {
            query = query.Where(l => l.AgencyId == id);
        }

        return await query
            .OrderBy(l => l.Kind)
            .ThenBy(l => l.Code)
            .Select(l => new LocationInfo(l.Id, l.AgencyId, l.Code, l.Name, (LocationKindInfo)l.Kind, l.Address))
            .ToListAsync(cancellationToken);
    }

    public async Task<LocationInfo?> FindLocationByCodeAsync(string code, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        var normalized = code.Trim().ToUpperInvariant();
        return await db.Set<Location>()
            .AsNoTracking()
            .Where(l => l.Code == normalized && l.IsActive)
            .Select(l => new LocationInfo(l.Id, l.AgencyId, l.Code, l.Name, (LocationKindInfo)l.Kind, l.Address))
            .FirstOrDefaultAsync(cancellationToken);
    }
}
