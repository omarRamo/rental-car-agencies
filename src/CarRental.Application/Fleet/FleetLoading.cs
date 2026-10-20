using System.Linq.Expressions;
using CarRental.Application.Common.Persistence;
using CarRental.Domain.Fleet;
using Microsoft.EntityFrameworkCore;

namespace CarRental.Application.Fleet;

/// <summary>Parts of the vehicle aggregate to load (the history is never loaded: it is only appended).</summary>
[Flags]
internal enum VehicleParts
{
    None = 0,
    Documents = 1,
    MaintenancePlans = 2,
    MaintenanceEvents = 4,
    Costs = 8,
    Photos = 16,
    Economics = 32,
    Availability = Documents | MaintenanceEvents,
    All = Documents | MaintenancePlans | MaintenanceEvents | Costs | Photos | Economics,
}

/// <summary>
/// Loads tracked vehicle aggregates: the roots, then each requested collection through the roots
/// (<c>SelectMany</c>, one query per collection, no cartesian product); EF Core fixes the navigations up.
/// </summary>
internal static class FleetLoading
{
    public static async Task<List<Vehicle>> LoadVehiclesAsync(
        this IAppDbContext db,
        Expression<Func<Vehicle, bool>> filter,
        VehicleParts parts,
        CancellationToken cancellationToken)
    {
        var roots = db.Set<Vehicle>().Where(filter);
        var vehicles = await (parts.HasFlag(VehicleParts.Economics) ? roots.Include(v => v.Economics) : roots).ToListAsync(cancellationToken);
        if (vehicles.Count == 0)
        {
            return vehicles;
        }

        if (parts.HasFlag(VehicleParts.Documents))
        {
            await roots.SelectMany(v => v.Documents).LoadAsync(cancellationToken);
        }

        if (parts.HasFlag(VehicleParts.MaintenancePlans))
        {
            await roots.SelectMany(v => v.MaintenancePlans).LoadAsync(cancellationToken);
        }

        if (parts.HasFlag(VehicleParts.MaintenanceEvents))
        {
            await roots.SelectMany(v => v.MaintenanceEvents).LoadAsync(cancellationToken);
        }

        if (parts.HasFlag(VehicleParts.Costs))
        {
            await roots.SelectMany(v => v.Costs).LoadAsync(cancellationToken);
        }

        if (parts.HasFlag(VehicleParts.Photos))
        {
            await roots.SelectMany(v => v.Photos).LoadAsync(cancellationToken);
        }

        return vehicles;
    }

    public static async Task<Vehicle?> LoadVehicleAsync(this IAppDbContext db, Guid vehicleId, VehicleParts parts, CancellationToken cancellationToken) =>
        (await db.LoadVehiclesAsync(v => v.Id == vehicleId, parts, cancellationToken)).FirstOrDefault();
}
