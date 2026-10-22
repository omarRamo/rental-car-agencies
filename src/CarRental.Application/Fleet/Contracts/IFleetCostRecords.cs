using CarRental.Domain.Common;

namespace CarRental.Application.Fleet.Contracts;

/// <summary>
/// Read API of the Fleet module for the expenses (package 3B): the maintenances of a vehicle, so that an expense can name
/// the one it pays — the cost of a maintenance then counts once, through the fleet, in the margins of the reports
/// (ADR-0063). No permission check (the caller checks its own).
/// </summary>
/// <remarks>A separate interface from <see cref="IFleetQueries"/>, so that its implementers are not affected.</remarks>
public interface IFleetCostRecords
{
    /// <summary>The maintenances of a vehicle (cancelled ones included), the latest first; empty for an unknown vehicle.</summary>
    Task<IReadOnlyList<MaintenanceRecordInfo>> GetMaintenancesAsync(Guid vehicleId, CancellationToken cancellationToken = default);
}

/// <summary>Kind of a maintenance (same values as the domain enum).</summary>
public enum MaintenanceTypeInfo
{
    OilChange = 1,
    Tyres = 2,
    Brakes = 3,
    Repair = 4,
    Other = 5,
}

/// <summary>Status of a maintenance (same values as the domain enum).</summary>
public enum MaintenanceStatusInfo
{
    Planned = 1,
    InProgress = 2,
    Completed = 3,
    Cancelled = 4,
}

/// <summary>
/// A maintenance of a vehicle: its kind, status, garage, dated block and cost (estimated while planned, actual once
/// completed — the cost the <c>fleet</c> source of the reports counts on the completion day).
/// </summary>
public sealed record MaintenanceRecordInfo(
    Guid Id,
    Guid VehicleId,
    MaintenanceTypeInfo Type,
    MaintenanceStatusInfo Status,
    string? Provider,
    string? Description,
    DateTimeOffset StartsAtUtc,
    DateTimeOffset? CompletedAtUtc,
    Money? Cost);
