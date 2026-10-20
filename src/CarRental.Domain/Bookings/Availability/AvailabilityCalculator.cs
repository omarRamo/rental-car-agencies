using CarRental.Domain.Common;

namespace CarRental.Domain.Bookings.Availability;

/// <summary>
/// Availability of a category for one channel (docs/conception/phase-1-exploitation.md Ãƒâ€šÃ‚Â§3, PROMPT Ãƒâ€šÃ‚Â§8): a window
/// <c>[s, e + buffer)</c> is available when <c>occupancy(t) + 1 ÃƒÂ¢Ã¢â‚¬Â°Ã‚Â¤ capacity(t)</c> for every instant t of the window.
/// </summary>
/// <remarks>
/// <para>Both sides are step functions: the headroom <c>capacity ÃƒÂ¢Ã‹â€ Ã¢â‚¬â„¢ occupancy</c> is computed once by merging their
/// sorted bounds (O(n log n) to build), then a window is checked by sweeping only the instants where it changes
/// (O(log n + k)). The calculator is immutable and thread-safe, so a snapshot can serve concurrent readers.</para>
/// <para>Outside the windows of the vehicles the capacity is 0: data that was not loaded can never look available.</para>
/// </remarks>
public sealed class AvailabilityCalculator
{
    private readonly StepFunction _headroom;
    private readonly StepFunction _stopSales;

    public AvailabilityCalculator(
        IEnumerable<RentableVehicle> vehicles,
        IEnumerable<OccupancyInterval> occupancy,
        ChannelRestrictions? restrictions = null)
        : this(CapacityTimeline.Fleet(vehicles), OccupancyTimeline.Of(occupancy), restrictions ?? ChannelRestrictions.None)
    {
    }

    public AvailabilityCalculator(StepFunction fleetCapacity, OccupancyTimeline occupancy, ChannelRestrictions restrictions)
    {
        ArgumentNullException.ThrowIfNull(fleetCapacity);
        ArgumentNullException.ThrowIfNull(occupancy);
        ArgumentNullException.ThrowIfNull(restrictions);

        FleetCapacity = fleetCapacity;
        Occupancy = occupancy;
        Restrictions = restrictions;
        Capacity = CapacityTimeline.ForChannel(fleetCapacity, restrictions);
        _stopSales = StepFunction.CountOf(restrictions.StopSales);
        _headroom = Capacity.Minus(occupancy.Counts);
    }

    /// <summary>Vehicles in service at each instant.</summary>
    public StepFunction FleetCapacity { get; }

    /// <summary>Capacity seen by the channel: fleet minus quotas of the other channels, 0 during a stop-sell.</summary>
    public StepFunction Capacity { get; }

    public OccupancyTimeline Occupancy { get; }

    public ChannelRestrictions Restrictions { get; }

    /// <summary>Checks a rental whose vehicle is not out yet.</summary>
    /// <param name="rental">Planned pick-up (included) to planned return (excluded).</param>
    /// <param name="preparationBuffer">Added after the return: the checked window is <c>[start, end + buffer)</c>.</param>
    /// <param name="excludedBookingId">The booking being amended, left out of the occupancy.</param>
    public AvailabilityCheck Check(Period rental, TimeSpan preparationBuffer, Guid? excludedBookingId = null)
    {
        ArgumentNullException.ThrowIfNull(rental);
        return CheckOccupancy(rental.ExtendEnd(preparationBuffer), excludedBookingId);
    }

    /// <summary>Checks an occupancy window: one more booking must fit at every instant of it.</summary>
    /// <param name="occupancy">The window to check, preparation buffer already included.</param>
    /// <param name="excludedBookingId">The booking being amended, left out of the occupancy.</param>
    public AvailabilityCheck CheckOccupancy(Period occupancy, Guid? excludedBookingId = null)
    {
        ArgumentNullException.ThrowIfNull(occupancy);

        var excluded = excludedBookingId is { } bookingId ? Occupancy.CountsOf(bookingId) : StepFunction.Zero;
        var end = occupancy.End.UtcTicks;
        var headroom = new StepCursor(_headroom, occupancy.Start.UtcTicks);
        var freed = new StepCursor(excluded, occupancy.Start.UtcTicks);
        var minimum = int.MaxValue;
        long conflictStart = 0, conflictEnd = 0;
        var conflict = ConflictState.None;

        for (var at = occupancy.Start.UtcTicks; at < end;)
        {
            var next = Math.Min(Math.Min(headroom.NextChange, freed.NextChange), end);
            var remaining = headroom.Value + freed.Value;
            minimum = Math.Min(minimum, remaining);
            if (remaining < 1)
            {
                if (conflict == ConflictState.None)
                {
                    (conflictStart, conflictEnd, conflict) = (at, next, ConflictState.Open);
                }
                else if (conflict == ConflictState.Open)
                {
                    conflictEnd = next;
                }
            }
            else if (conflict == ConflictState.Open)
            {
                conflict = ConflictState.Closed;
            }

            at = next;
            headroom.MoveTo(at);
            freed.MoveTo(at);
        }

        if (conflict == ConflictState.None)
        {
            return AvailabilityCheck.Available(occupancy, minimum);
        }

        var firstConflict = StepFunction.PeriodBetween(conflictStart, conflictEnd);
        return AvailabilityCheck.Unavailable(occupancy, minimum, firstConflict, ReasonAt(firstConflict.Start, excluded));
    }

    /// <summary>The capacity and occupancy of the category, as maximal segments (planning grid, search calendars).</summary>
    /// <param name="range">The period to describe; the segments cover it exactly.</param>
    /// <param name="excludedBookingId">A booking to leave out, e.g. the one being moved in the planning.</param>
    public IReadOnlyList<CapacitySegment> Profile(Period range, Guid? excludedBookingId = null)
    {
        ArgumentNullException.ThrowIfNull(range);

        var occupancy = excludedBookingId is { } bookingId && Occupancy.Contains(bookingId)
            ? Occupancy.Counts.Minus(Occupancy.CountsOf(bookingId))
            : Occupancy.Counts;
        var end = range.End.UtcTicks;
        var capacity = new StepCursor(Capacity, range.Start.UtcTicks);
        var occupied = new StepCursor(occupancy, range.Start.UtcTicks);
        var segments = new List<CapacitySegment>();
        var segmentStart = range.Start.UtcTicks;

        for (var at = range.Start.UtcTicks; at < end;)
        {
            var next = Math.Min(Math.Min(capacity.NextChange, occupied.NextChange), end);
            var (currentCapacity, currentOccupancy) = (capacity.Value, occupied.Value);
            at = next;
            capacity.MoveTo(at);
            occupied.MoveTo(at);

            // Close the segment when the pair of values changes (or at the end of the range).
            if (at == end || capacity.Value != currentCapacity || occupied.Value != currentOccupancy)
            {
                segments.Add(new CapacitySegment(StepFunction.PeriodBetween(segmentStart, at), currentCapacity, currentOccupancy));
                segmentStart = at;
            }
        }

        return segments;
    }

    private AvailabilityConflictReason ReasonAt(DateTimeOffset instant, StepFunction excluded)
    {
        var occupied = Occupancy.Counts.ValueAt(instant) - excluded.ValueAt(instant);
        if (FleetCapacity.ValueAt(instant) - occupied < 1)
        {
            return AvailabilityConflictReason.Capacity;
        }

        return _stopSales.ValueAt(instant) > 0 ? AvailabilityConflictReason.StopSale : AvailabilityConflictReason.Quota;
    }

    private enum ConflictState
    {
        None,
        Open,
        Closed,
    }
}
