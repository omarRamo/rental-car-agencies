using CarRental.Domain.Common;

namespace CarRental.Domain.Bookings.Availability;

/// <summary>Result of <see cref="GreedyVehicleAssigner"/>: proposals that the agent may accept or correct.</summary>
public sealed class VehicleAssignmentPlan
{
    internal VehicleAssignmentPlan(
        IReadOnlyList<VehicleAssignmentProposal> proposals,
        IReadOnlyList<UnassignableBooking> unassignable,
        IReadOnlyList<AssignmentConflict> conflicts)
    {
        Proposals = proposals;
        Unassignable = unassignable;
        Conflicts = conflicts;
    }

    /// <summary>A vehicle for each booking that could be assigned, in processing order (by start).</summary>
    public IReadOnlyList<VehicleAssignmentProposal> Proposals { get; }

    /// <summary>Bookings left without a vehicle, with the reason.</summary>
    public IReadOnlyList<UnassignableBooking> Unassignable { get; }

    /// <summary>
    /// Fixed assignments that break a rule, one entry per booking: overlap on their vehicle, vehicle outside the
    /// category, or vehicle not rentable over the booking.
    /// </summary>
    public IReadOnlyList<AssignmentConflict> Conflicts { get; }

    /// <summary>Every booking has a valid vehicle.</summary>
    public bool IsComplete => Unassignable.Count == 0 && Conflicts.Count == 0;
}

/// <summary>Proposed vehicle for a booking.</summary>
public sealed record VehicleAssignmentProposal(Guid BookingId, Guid VehicleId);

/// <summary>
/// A booking for which no vehicle could be proposed: <see cref="AvailabilityErrors.NoVehicleInService"/> or
/// <see cref="AvailabilityErrors.AllVehiclesOccupied"/>.
/// </summary>
public sealed record UnassignableBooking(Guid BookingId, Error Reason);

/// <summary>
/// A fixed assignment that breaks a rule: <see cref="AvailabilityErrors.VehicleOverlap"/>,
/// <see cref="AvailabilityErrors.VehicleNotInCategory"/> or <see cref="AvailabilityErrors.VehicleUnavailable"/>.
/// </summary>
public sealed record AssignmentConflict(Guid BookingId, Guid VehicleId, Error Reason);
