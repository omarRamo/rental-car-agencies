using System.Linq.Expressions;
using CarRental.Application.Common.Persistence;
using CarRental.Application.Fleet.Contracts;
using CarRental.Application.Fleet.Photos;
using CarRental.Domain.Common;
using CarRental.Domain.Fleet;
using Microsoft.EntityFrameworkCore;

namespace CarRental.Application.Fleet;

/// <summary>
/// The Fleet contract for the other modules (docs/conception/phase-1-exploitation.md §3 and §5). It often runs inside
/// the unit of work of the caller (e.g. a booking re-checking availability under the lock): every query is a
/// no-tracking projection, and windows and compliance are computed by the domain (<see cref="VehicleAvailability"/>,
/// <see cref="VehicleCompliance"/>) at the current instant.
/// </summary>
internal sealed class FleetQueries(IAppDbContext db, TimeProvider timeProvider) : IFleetQueries
{
    public async Task<IReadOnlyList<VehicleCategoryInfo>> GetCategoriesAsync(
        Guid? agencyId = null,
        bool includeInactive = false,
        CancellationToken cancellationToken = default)
    {
        var query = db.Set<VehicleCategory>().AsNoTracking();
        if (agencyId is { } agency)
        {
            query = query.Where(c => c.AgencyId == agency);
        }

        if (!includeInactive)
        {
            query = query.Where(c => c.IsActive);
        }

        var categories = await query.ToListAsync(cancellationToken);
        return [.. categories.OrderBy(c => c.DisplayOrder).ThenBy(c => c.Code.Value, StringComparer.Ordinal).Select(ToInfo)];
    }

    public async Task<VehicleCategoryInfo?> GetCategoryAsync(Guid categoryId, CancellationToken cancellationToken = default)
    {
        var category = await db.Set<VehicleCategory>().AsNoTracking().FirstOrDefaultAsync(c => c.Id == categoryId, cancellationToken);
        return category is null ? null : ToInfo(category);
    }

    public async Task<VehicleCategoryInfo?> FindCategoryByCodeAsync(Guid agencyId, string acrissCode, CancellationToken cancellationToken = default)
    {
        if (AcrissCode.Create(acrissCode) is not { IsSuccess: true } code)
        {
            return null;
        }

        var value = code.Value;
        var category = await db.Set<VehicleCategory>()
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.AgencyId == agencyId && c.IsActive && c.Code == value, cancellationToken);
        return category is null ? null : ToInfo(category);
    }

    public async Task<VehicleInfo?> GetVehicleAsync(Guid vehicleId, CancellationToken cancellationToken = default) =>
        await VehiclesAsync(v => v.Id == vehicleId, cancellationToken) is [var vehicle, ..] ? vehicle : null;

    public async Task<IReadOnlyList<VehicleInfo>> GetVehiclesAsync(Guid categoryId, CancellationToken cancellationToken = default) =>
        await VehiclesAsync(v => v.CategoryId == categoryId && v.Status != VehicleStatus.Sold, cancellationToken);

    public async Task<IReadOnlyList<VehicleAvailabilityInfo>> GetAvailabilityAsync(
        Guid categoryId,
        Period range,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(range);
        var vehicles = db.Set<Vehicle>().AsNoTracking().Where(v => v.CategoryId == categoryId && v.Status != VehicleStatus.Sold);
        var roots = await vehicles
            .Select(v => new { v.Id, v.PlateNumber, v.InServiceOn, v.Status, v.SoldAtUtc })
            .ToListAsync(cancellationToken);
        if (roots.Count == 0)
        {
            return [];
        }

        var compliance = await CompliantUntilAsync(vehicles, cancellationToken);
        var blocks = (await vehicles
                .SelectMany(v => v.MaintenanceEvents)
                .Where(e => e.Status != MaintenanceStatus.Cancelled)
                .Select(e => new { e.VehicleId, e.Status, e.StartsAtUtc, e.EndsAtUtc, e.CompletedAtUtc })
                .ToListAsync(cancellationToken))
            .Select(e => (e.VehicleId, Block: MaintenanceEvent.BlockOf(e.Status, e.StartsAtUtc, e.EndsAtUtc, e.CompletedAtUtc)))
            .Where(e => e.Block is not null)
            .ToLookup(e => e.VehicleId, e => e.Block!);

        var now = timeProvider.GetUtcNow();
        return
        [
            .. roots
                .OrderBy(v => v.PlateNumber, StringComparer.Ordinal)
                .Select(v => new VehicleAvailabilityInfo(
                    v.Id,
                    v.PlateNumber,
                    VehicleAvailability.Windows(
                        new VehicleAvailabilityProfile(v.InServiceOn, v.Status, v.SoldAtUtc, compliance.GetValueOrDefault(v.Id), [.. blocks[v.Id]]),
                        range,
                        now))),
        ];
    }

    public async Task<IReadOnlyList<VehicleDeadlineInfo>> GetDeadlinesAsync(Guid vehicleId, CancellationToken cancellationToken = default)
    {
        var documents = await db.Set<Vehicle>()
            .AsNoTracking()
            .Where(v => v.Id == vehicleId)
            .SelectMany(v => v.Documents)
            .Where(d => d.IsCurrent)
            .Select(d => new { d.Type, d.Number, d.ExpiresOn })
            .ToListAsync(cancellationToken);

        return
        [
            .. documents
                .OrderBy(d => d.Type)
                .Select(d => new VehicleDeadlineInfo(
                    (VehicleDocumentTypeInfo)d.Type,
                    d.Number,
                    d.ExpiresOn,
                    d.ExpiresOn is { } expiresOn ? VehicleCompliance.EndOfTunisDay(expiresOn) : null,
                    VehicleCompliance.IsMandatory(d.Type))),
        ];
    }

    internal static VehicleCategoryInfo ToInfo(VehicleCategory category) => new(
        category.Id,
        category.AgencyId,
        category.Code.Value,
        category.Name,
        category.ExampleModel,
        category.Seats,
        category.Doors,
        category.LargeBags,
        category.SmallBags,
        (TransmissionInfo)category.Transmission,
        (FuelTypeInfo)category.Fuel,
        category.AirConditioning,
        category.Deposit,
        category.ReducedDeposit,
        category.PhotoVersion is { } version ? FleetMedia.CategoryPhotoUrl(category.Id, version) : null,
        category.DisplayOrder,
        category.IsActive);

    private async Task<IReadOnlyList<VehicleInfo>> VehiclesAsync(Expression<Func<Vehicle, bool>> filter, CancellationToken cancellationToken)
    {
        var vehicles = db.Set<Vehicle>().AsNoTracking().Where(filter);
        var roots = await vehicles
            .Select(v => new
            {
                v.Id,
                v.AgencyId,
                v.CategoryId,
                v.PlateNumber,
                v.Make,
                v.Model,
                v.Year,
                v.Transmission,
                v.Fuel,
                v.Seats,
                v.Mileage,
                v.Status,
                v.GuaranteedModelDailySupplement,
            })
            .ToListAsync(cancellationToken);
        if (roots.Count == 0)
        {
            return [];
        }

        var compliance = await CompliantUntilAsync(vehicles, cancellationToken);
        return
        [
            .. roots
                .OrderBy(v => v.PlateNumber, StringComparer.Ordinal)
                .Select(v => new VehicleInfo(
                    v.Id,
                    v.AgencyId,
                    v.CategoryId,
                    v.PlateNumber,
                    v.Make,
                    v.Model,
                    v.Year,
                    (TransmissionInfo)v.Transmission,
                    (FuelTypeInfo)v.Fuel,
                    v.Seats,
                    v.Mileage,
                    (VehicleStatusInfo)v.Status,
                    v.GuaranteedModelDailySupplement,
                    compliance.GetValueOrDefault(v.Id))),
        ];
    }

    /// <summary>End of the compliance of each vehicle of <paramref name="vehicles"/> (absent when not compliant).</summary>
    private static async Task<Dictionary<Guid, DateTimeOffset?>> CompliantUntilAsync(IQueryable<Vehicle> vehicles, CancellationToken cancellationToken)
    {
        var documents = await vehicles
            .SelectMany(v => v.Documents)
            .Where(d => d.IsCurrent)
            .Select(d => new { d.VehicleId, d.Type, d.ExpiresOn })
            .ToListAsync(cancellationToken);
        return documents
            .GroupBy(d => d.VehicleId)
            .ToDictionary(g => g.Key, g => VehicleCompliance.CompliantUntil(g.Select(d => (d.Type, d.ExpiresOn))));
    }
}
