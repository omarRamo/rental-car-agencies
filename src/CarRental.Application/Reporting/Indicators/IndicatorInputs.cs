using CarRental.Domain.Common;
using CarRental.Domain.Reporting;

namespace CarRental.Application.Reporting.Indicators;

/// <summary>A stored snapshot row as the indicators read it.</summary>
internal sealed record SnapshotRowFigures(
    Guid VehicleId,
    string PlateNumber,
    string VehicleName,
    string CategoryCode,
    DateOnly Day,
    bool IsAvailable,
    bool IsRented,
    SalesChannel? Channel,
    Money Revenue,
    Money Commission,
    Money DirectCosts,
    Money Depreciation)
{
    public VehicleDayFigures Figures => new(VehicleId, Day, IsAvailable, IsRented, Revenue, Commission, DirectCosts, Depreciation);
}

/// <summary>A stored rental share of a vehicle-day as the indicators read it.</summary>
internal sealed record RentalShareFigures(Guid BookingId, SalesChannel Channel, DateOnly Day, Money Revenue, Money Commission);

/// <summary>Visits of a funnel step for a channel and a source (anonymous events of the public site).</summary>
internal sealed record FunnelCount(SalesChannel Channel, string? Source, FunnelStep Step, int Visits);
