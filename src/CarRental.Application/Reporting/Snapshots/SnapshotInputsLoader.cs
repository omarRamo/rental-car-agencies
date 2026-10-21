using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Fleet.Contracts;
using CarRental.Application.Reporting.Contracts;
using CarRental.Domain.Common;
using CarRental.Domain.Reporting;

namespace CarRental.Application.Reporting.Snapshots;

/// <summary>What the snapshots of a range of days are computed from.</summary>
internal sealed record SnapshotInputs(
    IReadOnlyList<FleetVehicleDay> Fleet,
    IReadOnlyList<RevenueRental> Rentals,
    IReadOnlyList<VehicleCostItem> Costs);

/// <summary>
/// Loads the inputs of the snapshots through the contracts of the other modules and the ports of the module (ADR-0045):
/// the vehicle-days of the Fleet module, the counted files of the Bookings module with their commission, and the costs of
/// every <see cref="IVehicleCostsSource"/>. The sources share the DbContext of the scope: one after the other.
/// </summary>
internal sealed class SnapshotInputsLoader(
    IFleetReportingQueries fleet,
    IBookingsReportingQueries bookings,
    IEnumerable<IVehicleCostsSource> costSources,
    IChannelCommissionSource commissions)
{
    public async Task<SnapshotInputs> LoadAsync(Guid agencyId, DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        var vehicles = await fleet.GetVehicleDaysAsync(agencyId, from, to, cancellationToken);
        var fleetDays = vehicles
            .SelectMany(v => v.Days.Select(d => new FleetVehicleDay(
                v.VehicleId,
                v.PlateNumber,
                $"{v.Make} {v.Model}",
                d.Day,
                d.CategoryId,
                d.CategoryCode,
                d.IsRentable,
                d.Depreciation)))
            .ToList();

        var counted = (await bookings.GetBookingsOverlappingAsync(agencyId, from, to, cancellationToken))
            .Where(b => RevenueRecognition.Counts(b.Status) && b.Quote.Days > 0)
            .ToList();
        var commission = counted.Count == 0
            ? new Dictionary<Guid, Money>()
            : await commissions.GetCommissionsAsync(agencyId, [.. counted.Select(b => b.BookingId)], cancellationToken);
        var rentals = counted
            .Select(b => new RevenueRental(
                b.BookingId,
                b.Number,
                b.Channel,
                b.PickupAt,
                b.Quote.Days,
                RevenueRecognition.RentalRevenue(b.Quote),
                commission.GetValueOrDefault(b.BookingId),
                [.. b.Vehicles.Select(v => new CarrierSpan(v.VehicleId, v.SinceUtc))]))
            .ToList();

        var costs = new List<VehicleCostItem>();
        foreach (var source in costSources)
        {
            costs.AddRange((await source.GetCostsAsync(agencyId, from, to, cancellationToken))
                .Select(c => new VehicleCostItem(c.VehicleId, c.Amount, c.From, c.To)));
        }

        return new SnapshotInputs(fleetDays, rentals, costs);
    }
}
