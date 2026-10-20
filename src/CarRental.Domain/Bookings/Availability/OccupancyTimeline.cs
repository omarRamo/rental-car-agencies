namespace CarRental.Domain.Bookings.Availability;

/// <summary>
/// Occupancy of a category as a step function of time — the number of occupancy intervals (buffer included) that
/// contain each instant — with the intervals of each booking, so that a booking can be left out when it is amended.
/// </summary>
public sealed class OccupancyTimeline
{
    private readonly Dictionary<Guid, List<OccupancyInterval>> _byBooking = new();

    private OccupancyTimeline(IReadOnlyList<OccupancyInterval> intervals)
    {
        Intervals = intervals;
        Counts = StepFunction.CountOf(intervals.Select(interval => interval.Period));
        foreach (var interval in intervals)
        {
            if (!_byBooking.TryGetValue(interval.BookingId, out var ofBooking))
            {
                ofBooking = [];
                _byBooking.Add(interval.BookingId, ofBooking);
            }

            ofBooking.Add(interval);
        }
    }

    public static OccupancyTimeline Empty { get; } = new([]);

    public IReadOnlyList<OccupancyInterval> Intervals { get; }

    /// <summary>Number of intervals containing each instant.</summary>
    public StepFunction Counts { get; }

    public static OccupancyTimeline Of(IEnumerable<OccupancyInterval> intervals)
    {
        ArgumentNullException.ThrowIfNull(intervals);
        return new OccupancyTimeline(
        [
            .. intervals.Select(interval => interval ?? throw new ArgumentNullException(nameof(intervals), "An interval cannot be null.")),
        ]);
    }

    public bool Contains(Guid bookingId) => _byBooking.ContainsKey(bookingId);

    /// <summary>The contribution of one booking (usually a single interval); zero for an unknown booking.</summary>
    public StepFunction CountsOf(Guid bookingId) =>
        _byBooking.TryGetValue(bookingId, out var intervals)
            ? StepFunction.CountOf(intervals.Select(interval => interval.Period))
            : StepFunction.Zero;
}
