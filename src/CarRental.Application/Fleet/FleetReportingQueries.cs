using System.Globalization;
using CarRental.Application.Common.Persistence;
using CarRental.Application.Fleet.Contracts;
using CarRental.Domain.Common;
using CarRental.Domain.Fleet;
using Microsoft.EntityFrameworkCore;

namespace CarRental.Application.Fleet;

/// <summary>
/// <see cref="IFleetReportingQueries"/>: no-tracking projections of the vehicles of an agency and of the parts of their
/// history the reports need (documents current and superseded, maintenance blocks, status and category changes,
/// economics); windows are judged by <see cref="FleetHistory"/>, depreciation by <see cref="VehicleEconomics"/>.
/// </summary>
internal sealed class FleetReportingQueries(IAppDbContext db) : IFleetReportingQueries
{
    public async Task<IReadOnlyList<FleetVehicleHistoryInfo>> GetVehicleDaysAsync(
        Guid agencyId,
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken = default)
    {
        if (to < from)
        {
            return [];
        }

        var rangeStart = TunisTime.StartOfDay(from);
        var vehicles = db.Set<Vehicle>()
            .AsNoTracking()
            .Where(v => v.AgencyId == agencyId && v.InServiceOn <= to && (v.SoldAtUtc == null || v.SoldAtUtc > rangeStart));
        var roots = await vehicles
            .Select(v => new { v.Id, v.PlateNumber, v.Make, v.Model, v.CategoryId, v.InServiceOn, v.SoldAtUtc, v.Status, v.StatusChangedAtUtc })
            .ToListAsync(cancellationToken);
        if (roots.Count == 0)
        {
            return [];
        }

        var documents = (await vehicles
                .SelectMany(v => v.Documents)
                .Select(d => new { d.VehicleId, d.Type, d.IssuedOn, d.ExpiresOn })
                .ToListAsync(cancellationToken))
            .ToLookup(d => d.VehicleId, d => new DocumentValidity(d.Type, d.IssuedOn, d.ExpiresOn));
        var blocks = (await vehicles
                .SelectMany(v => v.MaintenanceEvents)
                .Where(e => e.Status != MaintenanceStatus.Cancelled)
                .Select(e => new { e.VehicleId, e.Status, e.StartsAtUtc, e.EndsAtUtc, e.CompletedAtUtc })
                .ToListAsync(cancellationToken))
            .Select(e => (e.VehicleId, Block: MaintenanceEvent.BlockOf(e.Status, e.StartsAtUtc, e.EndsAtUtc, e.CompletedAtUtc)))
            .Where(e => e.Block is not null)
            .ToLookup(e => e.VehicleId, e => e.Block!);
        var vehicleIds = vehicles.Select(v => v.Id);
        var history = (await db.Set<VehicleHistoryEntry>()
                .AsNoTracking()
                .Where(h => vehicleIds.Contains(h.VehicleId) && (h.Kind == VehicleHistoryKind.StatusChanged || h.Kind == VehicleHistoryKind.CategoryChanged))
                .Select(h => new { h.VehicleId, h.Sequence, h.Kind, h.OccurredAtUtc, h.PreviousValue, h.NewValue })
                .ToListAsync(cancellationToken))
            .OrderBy(h => h.Sequence)
            .ToLookup(h => h.VehicleId);
        var economics = (await db.Set<VehicleEconomics>()
                .AsNoTracking()
                .Where(e => vehicleIds.Contains(e.VehicleId))
                .ToListAsync(cancellationToken))
            .ToDictionary(e => e.VehicleId);
        var codes = (await db.Set<VehicleCategory>()
                .AsNoTracking()
                .IgnoreQueryFilters()
                .Where(c => c.AgencyId == agencyId)
                .Select(c => new { c.Id, c.Code })
                .ToListAsync(cancellationToken))
            .ToDictionary(c => c.Id, c => c.Code.Value);

        var result = new List<FleetVehicleHistoryInfo>(roots.Count);
        foreach (var vehicle in roots.OrderBy(v => v.PlateNumber, StringComparer.Ordinal))
        {
            var first = vehicle.InServiceOn > from ? vehicle.InServiceOn : from;
            var last = to;
            DateOnly? soldOn = null;
            if (vehicle.SoldAtUtc is { } soldAt)
            {
                soldOn = TunisTime.DateOf(soldAt);
                if (soldOn < last)
                {
                    last = soldOn.Value;
                }

                // In the fleet on the day of the sale only if it was sold after the day began.
                if (TunisTime.StartOfDay(last) >= soldAt)
                {
                    last = last.AddDays(-1);
                }
            }

            if (last < first)
            {
                continue;
            }

            var vehicleHistory = history[vehicle.Id];
            var profile = new VehicleHistoryProfile(
                vehicle.InServiceOn,
                vehicle.SoldAtUtc,
                vehicle.Status,
                vehicle.StatusChangedAtUtc,
                [
                    .. vehicleHistory
                        .Where(h => h.Kind == VehicleHistoryKind.StatusChanged && Enum.TryParse<VehicleStatus>(h.NewValue, out _))
                        .Select(h => new VehicleStatusChange(h.OccurredAtUtc, Enum.Parse<VehicleStatus>(h.NewValue!))),
                ],
                [.. documents[vehicle.Id]],
                [.. blocks[vehicle.Id]],
                vehicle.CategoryId,
                [
                    .. vehicleHistory
                        .Where(h => h.Kind == VehicleHistoryKind.CategoryChanged
                                    && Guid.TryParse(h.PreviousValue, out _)
                                    && Guid.TryParse(h.NewValue, out _))
                        .Select(h => new VehicleCategoryChange(h.OccurredAtUtc, Guid.Parse(h.PreviousValue!), Guid.Parse(h.NewValue!))),
                ]);
            var range = Period.Create(TunisTime.StartOfDay(first), TunisTime.StartOfDay(last.AddDays(1))).Value;
            var windows = FleetHistory.Windows(profile, range);
            var vehicleEconomics = economics.GetValueOrDefault(vehicle.Id);

            var days = new List<FleetVehicleDayInfo>(last.DayNumber - first.DayNumber + 1);
            for (var day = first; day <= last; day = day.AddDays(1))
            {
                var dayStart = TunisTime.StartOfDay(day);
                var dayPeriod = Period.Create(dayStart, TunisTime.StartOfDay(day.AddDays(1))).Value;
                var category = FleetHistory.CategoryAt(profile, dayStart);
                days.Add(new FleetVehicleDayInfo(
                    day,
                    category,
                    codes.GetValueOrDefault(category, string.Empty),
                    windows.Any(w => w.Overlaps(dayPeriod)),
                    vehicleEconomics is null
                        ? Money.Zero
                        : vehicleEconomics.AccumulatedDepreciation(day.AddDays(1), soldOn) - vehicleEconomics.AccumulatedDepreciation(day, soldOn)));
            }

            result.Add(new FleetVehicleHistoryInfo(vehicle.Id, vehicle.PlateNumber, vehicle.Make, vehicle.Model, vehicle.InServiceOn, vehicle.SoldAtUtc, days));
        }

        return result;
    }

    public async Task<IReadOnlyList<FleetVehicleCostInfo>> GetVehicleCostsAsync(
        Guid agencyId,
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken = default)
    {
        if (to < from)
        {
            return [];
        }

        var vehicles = db.Set<Vehicle>().AsNoTracking().Where(v => v.AgencyId == agencyId);
        var start = TunisTime.StartOfDay(from);
        var end = TunisTime.StartOfDay(to.AddDays(1));

        var others = await vehicles
            .SelectMany(v => v.Costs)
            .Where(c => c.IncurredOn >= from && c.IncurredOn <= to)
            .Select(c => new { c.VehicleId, c.Label, c.Amount, c.IncurredOn })
            .ToListAsync(cancellationToken);
        var maintenance = await vehicles
            .SelectMany(v => v.MaintenanceEvents)
            .Where(e => e.Status == MaintenanceStatus.Completed && e.Cost != null && e.CompletedAtUtc >= start && e.CompletedAtUtc < end)
            .Select(e => new { e.VehicleId, e.Type, e.Cost, e.CompletedAtUtc })
            .ToListAsync(cancellationToken);
        var documents = await vehicles
            .SelectMany(v => v.Documents)
            .Where(d => d.Cost != null)
            .Select(d => new { d.VehicleId, d.Type, d.Number, d.Cost, d.IssuedOn, d.ExpiresOn, d.RecordedAtUtc })
            .ToListAsync(cancellationToken);

        var costs = new List<FleetVehicleCostInfo>();
        costs.AddRange(others.Select(c => new FleetVehicleCostInfo(c.VehicleId, FleetCostKindInfo.Other, c.Label, c.Amount, c.IncurredOn, c.IncurredOn)));
        costs.AddRange(maintenance.Select(e =>
        {
            var day = TunisTime.DateOf(e.CompletedAtUtc!.Value);
            return new FleetVehicleCostInfo(e.VehicleId, FleetCostKindInfo.Maintenance, e.Type.ToString(), e.Cost!.Value, day, day);
        }));
        foreach (var document in documents)
        {
            // A document costs over its validity: from its issue to its expiry day included. Without issue date, the year
            // before its expiry (insurance, road tax and inspection are yearly), else the day it was recorded.
            var validFrom = document.IssuedOn
                ?? (document.ExpiresOn is { } expiry ? expiry.AddYears(-1).AddDays(1) : TunisTime.DateOf(document.RecordedAtUtc));
            var validTo = document.ExpiresOn is { } expiresOn && expiresOn >= validFrom ? expiresOn : validFrom;
            if (validTo >= from && validFrom <= to)
            {
                costs.Add(new FleetVehicleCostInfo(
                    document.VehicleId,
                    FleetCostKindInfo.Document,
                    string.Create(CultureInfo.InvariantCulture, $"{document.Type} {document.Number}"),
                    document.Cost!.Value,
                    validFrom,
                    validTo));
            }
        }

        return [.. costs.Where(c => c.Amount.IsPositive).OrderBy(c => c.From).ThenBy(c => c.VehicleId)];
    }
}
