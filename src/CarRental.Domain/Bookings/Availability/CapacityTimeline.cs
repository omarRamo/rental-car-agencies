namespace CarRental.Domain.Bookings.Availability;

/// <summary>Capacity of a category as a step function of time (docs/conception/phase-1-exploitation.md §3).</summary>
public static class CapacityTimeline
{
    /// <summary>Fleet capacity: at each instant, the number of vehicles having a window that contains it.</summary>
    public static StepFunction Fleet(IEnumerable<RentableVehicle> vehicles) =>
        StepFunction.CountOf(RentableVehicle.Distinct(vehicles).SelectMany(vehicle => vehicle.Windows));

    /// <summary>
    /// Capacity seen by a channel: <c>max(0, fleet − vehicles reserved for the other channels)</c>, and 0 while a
    /// stop-sell of the channel is active.
    /// </summary>
    public static StepFunction ForChannel(StepFunction fleet, ChannelRestrictions restrictions)
    {
        ArgumentNullException.ThrowIfNull(fleet);
        ArgumentNullException.ThrowIfNull(restrictions);
        if (restrictions.IsEmpty)
        {
            return fleet;
        }

        var reserved = StepFunction.Sum(restrictions.ReservedForOtherChannels.Select(r => (r.Period, Weight: r.Vehicles)));
        var stopSales = StepFunction.CountOf(restrictions.StopSales);
        var afterQuotas = StepFunction.Combine(fleet, reserved, static (vehicles, kept) => Math.Max(0, vehicles - kept));
        return StepFunction.Combine(afterQuotas, stopSales, static (vehicles, stops) => stops > 0 ? 0 : vehicles);
    }
}
