using CarRental.Domain.Common;

namespace CarRental.Domain.Bookings.Availability;

/// <summary>
/// Whether one vehicle can carry one booking (PROMPT §6 invariants 1 and 3, docs/conception §3 "Affectation").
/// </summary>
public static class VehicleAssignmentRules
{
    /// <summary>
    /// Checks, in this order: no other booking of the vehicle overlaps the occupancy (buffer included — never waivable);
    /// the mandatory documents stay valid until the return (waivable by a traced manager decision for this booking);
    /// the vehicle can be rented over the occupancy (maintenance, status, sale, service start).
    /// </summary>
    /// <param name="vehicle">The vehicle and its rentable windows.</param>
    /// <param name="compliantUntil">End of the period covered by every mandatory document (Fleet contract), <see langword="null"/> when one is missing.</param>
    /// <param name="interval">The occupancy of the booking, buffer included.</param>
    /// <param name="returnAt">The planned return of the booking, compared with <paramref name="compliantUntil"/>.</param>
    /// <param name="vehicleIntervals">Occupancy intervals of the category; only the other bookings assigned to this vehicle count.</param>
    /// <param name="complianceWaived">The manager waived the document check for this booking.</param>
    /// <remarks>
    /// The Fleet windows end at the compliance end, so the windows are checked up to it: the buffer after a return
    /// on the last valid day is not a rental, and with a waiver the part after the expiry cannot be checked against
    /// later blocks (the manager's decision covers it). The capacity calculation stays prudent either way.
    /// </remarks>
    public static Result CanTake(
        RentableVehicle vehicle,
        DateTimeOffset? compliantUntil,
        OccupancyInterval interval,
        DateTimeOffset returnAt,
        IEnumerable<OccupancyInterval> vehicleIntervals,
        bool complianceWaived = false)
    {
        ArgumentNullException.ThrowIfNull(vehicle);
        ArgumentNullException.ThrowIfNull(interval);
        ArgumentNullException.ThrowIfNull(vehicleIntervals);

        if (vehicleIntervals.Any(other => other.AssignedVehicleId == vehicle.Id
                                          && other.BookingId != interval.BookingId
                                          && other.Period.Overlaps(interval.Period)))
        {
            return AvailabilityErrors.VehicleOverlap;
        }

        if (!complianceWaived && !IsCompliant(compliantUntil, returnAt))
        {
            return AvailabilityErrors.VehicleNotCompliant;
        }

        var checkedPart = PartBefore(interval.Period, compliantUntil);
        if (checkedPart is not null && !vehicle.IsRentableFor(checkedPart))
        {
            return AvailabilityErrors.VehicleUnavailable;
        }

        return Result.Success();
    }

    /// <summary>Whether the mandatory documents stay valid until <paramref name="returnAt"/> (same rule as the Fleet contract).</summary>
    public static bool IsCompliant(DateTimeOffset? compliantUntil, DateTimeOffset returnAt) =>
        compliantUntil is { } until && returnAt <= until;

    /// <summary>The part of <paramref name="period"/> before the compliance end, where Fleet windows are meaningful.</summary>
    private static Period? PartBefore(Period period, DateTimeOffset? compliantUntil)
    {
        if (compliantUntil is not { } until)
        {
            // No valid document at all (only reachable with a waiver): Fleet publishes no window to check.
            return null;
        }

        var end = until < period.End ? until : period.End;
        return end > period.Start ? Period.Create(period.Start, end).Value : null;
    }
}
