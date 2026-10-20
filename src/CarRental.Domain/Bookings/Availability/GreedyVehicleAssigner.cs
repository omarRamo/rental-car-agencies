using CarRental.Domain.Common;

namespace CarRental.Domain.Bookings.Availability;

/// <summary>
/// Proposes a vehicle for every unassigned booking of a category (docs/conception/phase-1-exploitation.md Â§3,
/// PROMPT Â§8). With interchangeable vehicles always in service, every set of bookings accepted by the
/// <see cref="AvailabilityCalculator"/> is fully assigned (interval partitioning); with maintenance windows and fixed
/// assignments a solution may exist that the greedy pass misses, and the agent corrects the plan.
/// </summary>
public static class GreedyVehicleAssigner
{
    /// <summary>
    /// Fixed assignments (an assigned vehicle, or the vehicle pinned by the "guaranteed model" option) are kept; the
    /// other bookings are taken by start (then end, then id) and each one gets, among the vehicles rentable over its
    /// whole interval and without an overlapping interval (buffer included), the vehicle freed the earliest â€” the end of
    /// its last interval before the booking, or the opening of its window â€” ties broken by plate number.
    /// </summary>
    /// <param name="vehicles">Vehicles of the category with their rentable windows, loaded over a range that covers the
    /// intervals (Fleet clips windows to the requested range): otherwise cut the intervals with <paramref name="notBefore"/>.</param>
    /// <param name="intervals">Occupancy intervals of the category, one per booking.</param>
    /// <param name="notBefore">When set, the intervals are cut at this instant and those ending before it are ignored
    /// (the past is settled, e.g. after a vehicle swap during a rental).</param>
    /// <remarks>
    /// A fixed assignment is reported when it overlaps an earlier fixed one on its vehicle, when the vehicle is not of
    /// the category, or when the vehicle cannot be rented over the whole booking (maintenance, sale, documents). A
    /// booking whose document check the manager waived therefore shows as unavailable: the caller knows the waivers.
    /// </remarks>
    public static VehicleAssignmentPlan Assign(
        IEnumerable<RentableVehicle> vehicles,
        IEnumerable<OccupancyInterval> intervals,
        DateTimeOffset? notBefore = null)
    {
        ArgumentNullException.ThrowIfNull(intervals);

        var fleet = RentableVehicle.Distinct(vehicles)
            .OrderBy(vehicle => vehicle.PlateNumber, StringComparer.Ordinal)
            .ThenBy(vehicle => vehicle.Id)
            .ToList();
        var byId = fleet.ToDictionary(vehicle => vehicle.Id);
        var schedules = fleet.ToDictionary(vehicle => vehicle.Id, _ => new List<Period>());
        var bookings = Prepare(intervals, notBefore);

        var conflicts = new List<AssignmentConflict>();
        foreach (var booking in bookings.Where(b => b.IsAssigned))
        {
            var vehicleId = booking.AssignedVehicleId!.Value;
            if (!schedules.TryGetValue(vehicleId, out var schedule))
            {
                conflicts.Add(new AssignmentConflict(booking.BookingId, vehicleId, AvailabilityErrors.VehicleNotInCategory));
                continue;
            }

            if (schedule.Exists(period => period.Overlaps(booking.Period)))
            {
                conflicts.Add(new AssignmentConflict(booking.BookingId, vehicleId, AvailabilityErrors.VehicleOverlap));
            }
            else if (!byId[vehicleId].IsRentableFor(booking.Period))
            {
                conflicts.Add(new AssignmentConflict(booking.BookingId, vehicleId, AvailabilityErrors.VehicleUnavailable));
            }

            // A fixed booking keeps its vehicle busy even when it breaks a rule: proposals never overlap it.
            schedule.Add(booking.Period);
        }

        var proposals = new List<VehicleAssignmentProposal>();
        var unassignable = new List<UnassignableBooking>();
        foreach (var booking in bookings.Where(b => !b.IsAssigned))
        {
            RentableVehicle? best = null;
            var bestFreedAt = DateTimeOffset.MaxValue;
            var anyInService = false;
            foreach (var vehicle in fleet)
            {
                if (vehicle.WindowContaining(booking.Period) is not { } window)
                {
                    continue;
                }

                anyInService = true;
                var schedule = schedules[vehicle.Id];
                if (schedule.Exists(period => period.Overlaps(booking.Period)))
                {
                    continue;
                }

                // Strictly earlier only: the fleet is in plate order, so ties keep the first plate.
                var freedAt = FreedAt(window, schedule, booking.Period.Start);
                if (best is null || freedAt < bestFreedAt)
                {
                    best = vehicle;
                    bestFreedAt = freedAt;
                }
            }

            if (best is null)
            {
                unassignable.Add(new UnassignableBooking(
                    booking.BookingId,
                    anyInService ? AvailabilityErrors.AllVehiclesOccupied : AvailabilityErrors.NoVehicleInService));
                continue;
            }

            proposals.Add(new VehicleAssignmentProposal(booking.BookingId, best.Id));
            schedules[best.Id].Add(booking.Period);
        }

        return new VehicleAssignmentPlan(proposals, unassignable, conflicts);
    }

    /// <summary>When the vehicle became free before <paramref name="start"/>: its last interval end or its window opening.</summary>
    private static DateTimeOffset FreedAt(Period window, List<Period> schedule, DateTimeOffset start)
    {
        var freedAt = window.Start;
        foreach (var period in schedule)
        {
            if (period.End <= start && period.End > freedAt)
            {
                freedAt = period.End;
            }
        }

        return freedAt;
    }

    private static List<OccupancyInterval> Prepare(IEnumerable<OccupancyInterval> intervals, DateTimeOffset? notBefore)
    {
        var seen = new HashSet<Guid>();
        var prepared = new List<OccupancyInterval>();
        foreach (var interval in intervals)
        {
            ArgumentNullException.ThrowIfNull(interval, nameof(intervals));
            if (!seen.Add(interval.BookingId))
            {
                throw new ArgumentException($"Booking {interval.BookingId} has more than one occupancy interval.", nameof(intervals));
            }

            if (notBefore is not { } cut || interval.Period.Start >= cut)
            {
                prepared.Add(interval);
            }
            else if (interval.Period.End > cut)
            {
                prepared.Add(new OccupancyInterval(interval.BookingId, Period.Create(cut, interval.Period.End).Value, interval.AssignedVehicleId));
            }
        }

        return
        [
            .. prepared
                .OrderBy(interval => interval.Period.Start)
                .ThenBy(interval => interval.Period.End)
                .ThenBy(interval => interval.BookingId),
        ];
    }
}
