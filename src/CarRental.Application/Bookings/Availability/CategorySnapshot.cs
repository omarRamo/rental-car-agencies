using CarRental.Domain.Bookings.Availability;
using CarRental.Domain.Common;

namespace CarRental.Application.Bookings.Availability;

/// <summary>
/// Immutable availability data of one category over <see cref="Coverage"/>: vehicles, occupancy and the calculators of
/// the loaded channels. Shared by concurrent readers when cached.
/// </summary>
internal sealed class CategorySnapshot
{
    private readonly AvailabilityCalculator _wholeFleet;
    private readonly Dictionary<SalesChannel, AvailabilityCalculator> _byChannel;

    public CategorySnapshot(
        CategoryContext context,
        Period coverage,
        IReadOnlyList<RentableVehicle> vehicles,
        IReadOnlyList<OccupancyInterval> occupancy,
        IReadOnlyDictionary<SalesChannel, ChannelRestrictions> restrictions)
    {
        Context = context;
        Coverage = coverage;
        Vehicles = vehicles;

        var fleet = CapacityTimeline.Fleet(vehicles);
        var timeline = OccupancyTimeline.Of(occupancy);
        _wholeFleet = new AvailabilityCalculator(fleet, timeline, ChannelRestrictions.None);
        _byChannel = restrictions.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.IsEmpty ? _wholeFleet : new AvailabilityCalculator(fleet, timeline, pair.Value));
    }

    public CategoryContext Context { get; }

    public OccupancyBasis Basis => Context.Basis;

    /// <summary>The range over which vehicle windows and occupancy were loaded: answers are only valid inside it.</summary>
    public Period Coverage { get; }

    public IReadOnlyList<RentableVehicle> Vehicles { get; }

    public int IntervalCount => _wholeFleet.Occupancy.Intervals.Count;

    public bool Covers(Period window) => Coverage.Contains(window);

    /// <summary>The calculator of a loaded channel, or of the whole fleet (no restriction) for <see langword="null"/>.</summary>
    public AvailabilityCalculator CalculatorFor(SalesChannel? channel)
    {
        if (channel is not { } value)
        {
            return _wholeFleet;
        }

        return _byChannel.TryGetValue(value, out var calculator)
            ? calculator
            : throw new InvalidOperationException($"The restrictions of channel {value} were not loaded in this snapshot.");
    }
}
