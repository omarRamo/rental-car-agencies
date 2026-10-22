using CarRental.Application.Common.Persistence;
using CarRental.Application.Fleet.Contracts;
using CarRental.Domain.Fleet;
using Microsoft.EntityFrameworkCore;

namespace CarRental.Application.Fleet;

/// <summary><see cref="IFleetCostRecords"/>: no-tracking projection of the maintenances of a vehicle.</summary>
internal sealed class FleetCostRecords(IAppDbContext db) : IFleetCostRecords
{
    public async Task<IReadOnlyList<MaintenanceRecordInfo>> GetMaintenancesAsync(Guid vehicleId, CancellationToken cancellationToken = default)
    {
        var maintenances = await db.Set<Vehicle>()
            .AsNoTracking()
            .Where(v => v.Id == vehicleId)
            .SelectMany(v => v.MaintenanceEvents)
            .Select(e => new { e.Id, e.VehicleId, e.Type, e.Status, e.Provider, e.Description, e.StartsAtUtc, e.CompletedAtUtc, e.Cost })
            .ToListAsync(cancellationToken);
        return
        [
            .. maintenances
                .OrderByDescending(e => e.StartsAtUtc)
                .Select(e => new MaintenanceRecordInfo(
                    e.Id,
                    e.VehicleId,
                    (MaintenanceTypeInfo)(int)e.Type,
                    (MaintenanceStatusInfo)(int)e.Status,
                    e.Provider,
                    e.Description,
                    e.StartsAtUtc,
                    e.CompletedAtUtc,
                    e.Cost)),
        ];
    }
}
