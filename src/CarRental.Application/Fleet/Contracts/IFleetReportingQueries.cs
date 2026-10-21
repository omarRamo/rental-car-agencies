using CarRental.Domain.Common;

namespace CarRental.Application.Fleet.Contracts;

/// <summary>
/// Read API of the Fleet module for the Reporting module (daily snapshots and indicators, ADR-0045): the vehicles of an
/// agency day by day — category, whether they could be rented, depreciation — and the costs recorded on them. Unlike
/// <see cref="IFleetQueries.GetAvailabilityAsync"/>, which evaluates the windows at the current instant, past days are
/// judged on the history of the vehicle (its status changes, the documents valid on that day, its maintenance blocks), so
/// that a day computed again later gives the same answer. No permission check (the caller checks its own).
/// </summary>
/// <remarks>A separate interface from <see cref="IFleetQueries"/>, so that its implementers are not affected.</remarks>
public interface IFleetReportingQueries
{
    /// <summary>
    /// The vehicles of the agency in the fleet on at least one of the Tunis days <paramref name="from"/> to
    /// <paramref name="to"/> (sold ones included), ordered by plate, each with those of the days when it was in the fleet:
    /// from its in-service day to the day of its sale.
    /// </summary>
    Task<IReadOnlyList<FleetVehicleHistoryInfo>> GetVehicleDaysAsync(
        Guid agencyId,
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The costs recorded on the vehicles of the agency whose period overlaps the Tunis days <paramref name="from"/> to
    /// <paramref name="to"/>: the price of each document (premium, road tax, inspection) over its validity — from its issue
    /// day, or the year before its expiry when the issue day is unknown, to its expiry day —, the actual cost of each
    /// completed maintenance on its completion day, each other cost on its day. Amounts as recorded (VAT included).
    /// </summary>
    Task<IReadOnlyList<FleetVehicleCostInfo>> GetVehicleCostsAsync(
        Guid agencyId,
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken = default);
}

/// <summary>A vehicle and its days in the fleet within the requested range.</summary>
public sealed record FleetVehicleHistoryInfo(
    Guid VehicleId,
    string PlateNumber,
    string Make,
    string Model,
    DateOnly InServiceOn,
    DateTimeOffset? SoldAtUtc,
    IReadOnlyList<FleetVehicleDayInfo> Days);

/// <summary>
/// A Tunis day of a vehicle in the fleet: its category that day, whether it could be rented for at least part of the day
/// (in service, not sold yet, every mandatory document valid, outside maintenance blocks and out-of-service periods), and
/// its straight-line depreciation of the day (zero without economic data, after the depreciation period and from the sale).
/// </summary>
public sealed record FleetVehicleDayInfo(DateOnly Day, Guid CategoryId, string CategoryCode, bool IsRentable, Money Depreciation);

/// <summary>Where a cost of a vehicle comes from.</summary>
public enum FleetCostKindInfo
{
    /// <summary>The price of a document (insurance premium, road tax, technical inspection, registration card).</summary>
    Document = 1,

    /// <summary>The actual cost of a completed maintenance.</summary>
    Maintenance = 2,

    /// <summary>An other cost recorded on the vehicle (registration fees, accessories…).</summary>
    Other = 3,
}

/// <summary>A cost of a vehicle covering the Tunis days <paramref name="From"/> to <paramref name="To"/> (both included).</summary>
public sealed record FleetVehicleCostInfo(Guid VehicleId, FleetCostKindInfo Kind, string Label, Money Amount, DateOnly From, DateOnly To);
