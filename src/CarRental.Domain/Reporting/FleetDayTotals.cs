using CarRental.Domain.Common;

namespace CarRental.Domain.Reporting;

/// <summary>What the fleet indicators need of a snapshot row (projected by the queries).</summary>
public sealed record VehicleDayFigures(
    Guid VehicleId,
    DateOnly Day,
    bool IsAvailable,
    bool IsRented,
    Money Revenue,
    Money Commission,
    Money DirectCosts,
    Money Depreciation);

/// <summary>
/// Totals of vehicle-days and the fleet indicators of SPEC §8 computed from them (ADR-0046). The ratios hold together:
/// revenue per available vehicle-day = utilization × average price per rented day.
/// </summary>
public sealed record FleetDayTotals(
    int VehicleDays,
    int AvailableDays,
    int RentedDays,
    Money Revenue,
    Money Commission,
    Money DirectCosts,
    Money Depreciation)
{
    public static FleetDayTotals Empty { get; } = new(0, 0, 0, Money.Zero, Money.Zero, Money.Zero, Money.Zero);

    /// <summary>Revenue after the commissions of the sales channels.</summary>
    public Money NetRevenue => Revenue - Commission;

    /// <summary>Direct costs and depreciation.</summary>
    public Money Costs => DirectCosts + Depreciation;

    /// <summary>Margin = revenue − commissions − direct costs − depreciation.</summary>
    public Money Margin => Revenue - Commission - DirectCosts - Depreciation;

    /// <summary>Revenu par véhicule disponible et par jour: rental revenue ÷ available vehicle-days (the manager's main indicator).</summary>
    public Money? RevenuePerAvailableDay => KpiMath.PerUnit(Revenue, AvailableDays);

    /// <summary>Taux d'utilisation: rented vehicle-days ÷ available vehicle-days.</summary>
    public decimal? Utilization => KpiMath.Ratio(RentedDays, AvailableDays);

    /// <summary>Prix moyen par jour loué: rental revenue ÷ rented vehicle-days.</summary>
    public Money? AverageRentedDayPrice => KpiMath.PerUnit(Revenue, RentedDays);

    /// <summary>Margin per available vehicle-day.</summary>
    public Money? MarginPerAvailableDay => KpiMath.PerUnit(Margin, AvailableDays);

    /// <summary>Totals of snapshot rows: every row is a vehicle-day; available and rented ones are counted apart.</summary>
    public static FleetDayTotals Of(IEnumerable<VehicleDayFigures> days)
    {
        ArgumentNullException.ThrowIfNull(days);
        var vehicleDays = 0;
        var available = 0;
        var rented = 0;
        var revenue = Money.Zero;
        var commission = Money.Zero;
        var costs = Money.Zero;
        var depreciation = Money.Zero;
        foreach (var day in days)
        {
            vehicleDays++;
            available += day.IsAvailable ? 1 : 0;
            rented += day.IsRented ? 1 : 0;
            revenue += day.Revenue;
            commission += day.Commission;
            costs += day.DirectCosts;
            depreciation += day.Depreciation;
        }

        return new FleetDayTotals(vehicleDays, available, rented, revenue, commission, costs, depreciation);
    }

    public FleetDayTotals Add(FleetDayTotals other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return new FleetDayTotals(
            VehicleDays + other.VehicleDays,
            AvailableDays + other.AvailableDays,
            RentedDays + other.RentedDays,
            Revenue + other.Revenue,
            Commission + other.Commission,
            DirectCosts + other.DirectCosts,
            Depreciation + other.Depreciation);
    }
}
