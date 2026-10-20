using CarRental.Application.Bookings.Administration;
using CarRental.Application.Bookings.Availability;
using CarRental.Application.Bookings.Writing;
using CarRental.Application.Fleet.Contracts;
using CarRental.Application.Identity.Contracts;
using CarRental.Domain.Bookings;
using CarRental.Domain.Bookings.Availability;
using CarRental.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace CarRental.Application.Bookings;

/// <summary>
/// Vehicle assignment of the rental files (docs/conception §3 "Affectation", PROMPT §6 invariants 1 and 3): assign,
/// unassign, swap during the rental, compliance waiver of a manager, and the proposals of the greedy assigner. Every
/// assignment is checked by the availability guard under the lock of the category, on fresh data.
/// </summary>
public sealed class VehicleAssignmentService
{
    private static readonly BookingStatus[] Planned = [.. BookingStatusRules.Planned];

    private readonly BookingWriter _writer;
    private readonly AvailabilitySnapshotLoader _loader;
    private readonly IFleetQueries _fleet;

    internal VehicleAssignmentService(BookingWriter writer, AvailabilitySnapshotLoader loader, IFleetQueries fleet)
    {
        _writer = writer;
        _loader = loader;
        _fleet = fleet;
    }

    /// <summary>
    /// Assigns <paramref name="vehicleId"/>. <paramref name="waiverReason"/> waives the documents check of this vehicle for
    /// this file (permission <c>bookings.compliance.waive</c>, traced); <paramref name="swapReason"/> is required to swap
    /// the vehicle of a rental in progress. An overlap (buffer included) is never waivable.
    /// </summary>
    public async Task<Result> AssignAsync(
        Guid bookingId,
        Guid vehicleId,
        string? waiverReason = null,
        string? swapReason = null,
        CancellationToken cancellationToken = default)
    {
        if (!_writer.User.HasPermission(Permissions.BookingsManage)
            || (waiverReason is not null && !_writer.User.HasPermission(Permissions.BookingsComplianceWaive)))
        {
            return BookingErrors.Forbidden;
        }

        return await _writer.TransitionAsync(
            bookingId,
            async (booking, now, ct) =>
            {
                var keepsWaiver = booking.AssignedVehicleId == vehicleId && booking.IsComplianceWaived;
                var checkable = BookingStateMachine.CanApply(BookingCommand.AssignVehicle, booking.Status);
                if (checkable)
                {
                    var assignable = await _writer.Guard.EnsureVehicleAssignableAsync(
                        new VehicleAssignmentRequest(booking.CategoryId, booking.ToOccupancy(), vehicleId)
                        {
                            ComplianceWaived = waiverReason is not null || keepsWaiver,
                        },
                        ct);
                    if (assignable.IsFailure)
                    {
                        return assignable;
                    }
                }

                return booking.AssignVehicle(vehicleId, waiverReason, swapReason, _writer.Actor(), now);
            },
            cancellationToken);
    }

    /// <summary>Unassigns the vehicle of a file before its departure.</summary>
    public async Task<Result> UnassignAsync(Guid bookingId, CancellationToken cancellationToken = default)
    {
        if (!_writer.User.HasPermission(Permissions.BookingsManage))
        {
            return BookingErrors.Forbidden;
        }

        return await _writer.TransitionAsync(
            bookingId,
            (booking, now, _) => Task.FromResult(booking.UnassignVehicle(_writer.Actor(), now)),
            cancellationToken);
    }

    /// <summary>
    /// The vehicles of the category of a file with a pre-check of the assignment on fresh data (overlap, documents,
    /// rentable windows), for the assignment dialog; the guard decides again under the lock when assigning.
    /// </summary>
    public async Task<Result<IReadOnlyList<VehicleCandidate>>> GetCandidatesAsync(Guid bookingId, CancellationToken cancellationToken = default)
    {
        if (!_writer.User.HasPermission(Permissions.BookingsRead))
        {
            return BookingErrors.Forbidden;
        }

        var booking = await _writer.Db.Set<Booking>().AsNoTracking().FirstOrDefaultAsync(b => b.Id == bookingId, cancellationToken);
        if (booking is null)
        {
            return BookingErrors.NotFound;
        }

        var context = await _loader.LoadContextAsync(booking.CategoryId, cancellationToken);
        if (context is null)
        {
            return AvailabilityErrors.CategoryNotFound;
        }

        var vehicles = await _fleet.GetVehiclesAsync(booking.CategoryId, cancellationToken);
        var occupancy = context.Basis.PeriodOf(booking.ToOccupancy());
        var window = occupancy is null ? null : (occupancy.End <= context.Basis.Now ? null : Period.Create(occupancy.Start > context.Basis.Now ? occupancy.Start : context.Basis.Now, occupancy.End).Value);
        if (window is null)
        {
            return Result.Success<IReadOnlyList<VehicleCandidate>>([.. vehicles.Select(v => Candidate(v, VehicleCandidateState.Free, booking))]);
        }

        var rentable = await _loader.LoadVehiclesAsync(booking.CategoryId, window, cancellationToken);
        var intervals = await _loader.LoadOccupancyAsync(context, window, booking.Id, cancellationToken);
        var candidates = new List<VehicleCandidate>(vehicles.Count);
        foreach (var vehicle in vehicles)
        {
            var windows = rentable.FirstOrDefault(r => r.Id == vehicle.Id) ?? new RentableVehicle(vehicle.Id, vehicle.PlateNumber, []);
            var check = VehicleAssignmentRules.CanTake(
                windows,
                vehicle.CompliantUntil,
                new OccupancyInterval(booking.Id, window, vehicle.Id),
                context.Basis.VehicleBackAt(booking.ToOccupancy()),
                intervals,
                booking.IsComplianceWaived && booking.AssignedVehicleId == vehicle.Id);
            var state = check.IsSuccess
                ? VehicleCandidateState.Free
                : check.Error == AvailabilityErrors.VehicleOverlap
                    ? VehicleCandidateState.Overlap
                    : check.Error == AvailabilityErrors.VehicleNotCompliant ? VehicleCandidateState.NotCompliant : VehicleCandidateState.Unavailable;
            candidates.Add(Candidate(vehicle, state, booking));
        }

        return candidates;
    }

    /// <summary>
    /// The proposals of the greedy assigner for the files of an agency over <paramref name="range"/> (fixed assignments
    /// kept, intervals cut at the range start), with the files that cannot be placed and the fixed assignments in conflict.
    /// </summary>
    public async Task<Result<AssignmentPlanView>> ProposeAsync(Guid agencyId, Period range, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(range);
        if (!_writer.User.HasPermission(Permissions.BookingsRead))
        {
            return BookingErrors.Forbidden;
        }

        var proposals = new List<AssignmentProposalView>();
        var unassignable = new List<AssignmentIssueView>();
        var conflicts = new List<AssignmentIssueView>();
        foreach (var category in await _fleet.GetCategoriesAsync(agencyId, includeInactive: false, cancellationToken))
        {
            var context = await _loader.LoadContextAsync(category.Id, cancellationToken);
            if (context is null)
            {
                continue;
            }

            var vehicles = await _loader.LoadVehiclesAsync(category.Id, range, cancellationToken);
            var intervals = await _loader.LoadOccupancyAsync(context, range, excludedBookingId: null, cancellationToken);
            var plan = GreedyVehicleAssigner.Assign(vehicles, intervals, notBefore: range.Start);
            if (plan.Proposals.Count == 0 && plan.Unassignable.Count == 0 && plan.Conflicts.Count == 0)
            {
                continue;
            }

            var bookingIds = plan.Proposals.Select(p => p.BookingId)
                .Concat(plan.Unassignable.Select(u => u.BookingId))
                .Concat(plan.Conflicts.Select(c => c.BookingId))
                .ToList();
            var bookings = await _writer.Db.Set<Booking>()
                .AsNoTracking()
                .Where(b => bookingIds.Contains(b.Id))
                .Select(b => new { b.Id, b.Number, b.PickupAt, b.ReturnAt, b.Status })
                .ToDictionaryAsync(b => b.Id, cancellationToken);
            var plates = vehicles.ToDictionary(v => v.Id, v => v.PlateNumber);

            // Only files not handed over yet are proposed: a rental in progress always has its vehicle.
            foreach (var proposal in plan.Proposals.Where(p => bookings.TryGetValue(p.BookingId, out var b) && Planned.Contains(b.Status)))
            {
                var booking = bookings[proposal.BookingId];
                proposals.Add(new AssignmentProposalView(
                    proposal.BookingId,
                    booking.Number,
                    category.Id,
                    category.AcrissCode,
                    booking.PickupAt,
                    booking.ReturnAt,
                    proposal.VehicleId,
                    plates.GetValueOrDefault(proposal.VehicleId, string.Empty)));
            }

            unassignable.AddRange(plan.Unassignable
                .Where(u => bookings.ContainsKey(u.BookingId))
                .Select(u => new AssignmentIssueView(u.BookingId, bookings[u.BookingId].Number, category.AcrissCode, null, null, u.Reason)));
            conflicts.AddRange(plan.Conflicts
                .Where(c => bookings.ContainsKey(c.BookingId))
                .Select(c => new AssignmentIssueView(c.BookingId, bookings[c.BookingId].Number, category.AcrissCode, c.VehicleId, plates.GetValueOrDefault(c.VehicleId), c.Reason)));
        }

        return new AssignmentPlanView(range, proposals, unassignable, conflicts);
    }

    /// <summary>Applies reviewed proposals one by one (each under the lock); a refused one does not stop the others.</summary>
    public async Task<Result<IReadOnlyList<AssignmentOutcome>>> ApplyAsync(
        IReadOnlyList<AssignmentChoice> choices,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(choices);
        if (!_writer.User.HasPermission(Permissions.BookingsManage))
        {
            return BookingErrors.Forbidden;
        }

        var outcomes = new List<AssignmentOutcome>(choices.Count);
        foreach (var choice in choices)
        {
            var assigned = await AssignAsync(choice.BookingId, choice.VehicleId, cancellationToken: cancellationToken);
            outcomes.Add(new AssignmentOutcome(choice.BookingId, choice.VehicleId, assigned.IsSuccess ? null : assigned.Error));
        }

        return outcomes;
    }

    private static VehicleCandidate Candidate(VehicleInfo vehicle, VehicleCandidateState state, Booking booking) => new(
        vehicle.Id,
        vehicle.PlateNumber,
        vehicle.Make,
        vehicle.Model,
        vehicle.Status,
        vehicle.CompliantUntil,
        vehicle.Status == VehicleStatusInfo.Sold ? VehicleCandidateState.Unavailable : state,
        vehicle.Id == booking.AssignedVehicleId);
}
