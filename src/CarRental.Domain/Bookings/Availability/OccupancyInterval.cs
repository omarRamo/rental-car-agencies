using CarRental.Domain.Common;

namespace CarRental.Domain.Bookings.Availability;

/// <summary>
/// The half-open period during which a booking consumes one unit of capacity of its category, preparation buffer
/// included, and the vehicle it occupies when it is assigned (or pinned by the "guaranteed model" option).
/// </summary>
public sealed record OccupancyInterval
{
    public OccupancyInterval(Guid bookingId, Period period, Guid? assignedVehicleId = null)
    {
        if (bookingId == Guid.Empty)
        {
            throw new ArgumentException("A booking id is required.", nameof(bookingId));
        }

        ArgumentNullException.ThrowIfNull(period);
        if (assignedVehicleId == Guid.Empty)
        {
            throw new ArgumentException("An assigned vehicle id cannot be empty.", nameof(assignedVehicleId));
        }

        BookingId = bookingId;
        Period = period;
        AssignedVehicleId = assignedVehicleId;
    }

    public Guid BookingId { get; }

    /// <summary>Occupied period, preparation buffer included.</summary>
    public Period Period { get; }

    /// <summary>The occupied vehicle; <see langword="null"/> while the booking is not assigned.</summary>
    public Guid? AssignedVehicleId { get; }

    public bool IsAssigned => AssignedVehicleId is not null;

    /// <summary>The same occupancy carried by <paramref name="vehicleId"/>.</summary>
    public OccupancyInterval AssignedTo(Guid vehicleId) => new(BookingId, Period, vehicleId);
}
