using CarRental.Domain.Common;

namespace CarRental.Domain.Bookings.Availability;

/// <summary>
/// The status-independent data that decides how a booking occupies capacity: its <see cref="OccupancyKind"/>, the
/// planned rental <c>[PickupAt, ReturnAt)</c>, the actual hand-over times and the assigned (or guaranteed) vehicle.
/// <see cref="OccupancyBasis.IntervalOf(BookingOccupancy)"/> turns it into an <see cref="OccupancyInterval"/>.
/// </summary>
public sealed record BookingOccupancy
{
    public BookingOccupancy(
        Guid bookingId,
        OccupancyKind kind,
        Period rental,
        DateTimeOffset? actualPickupAt = null,
        DateTimeOffset? actualReturnAt = null,
        Guid? assignedVehicleId = null)
    {
        if (bookingId == Guid.Empty)
        {
            throw new ArgumentException("A booking id is required.", nameof(bookingId));
        }

        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown occupancy kind.");
        }

        ArgumentNullException.ThrowIfNull(rental);
        if (assignedVehicleId == Guid.Empty)
        {
            throw new ArgumentException("An assigned vehicle id cannot be empty.", nameof(assignedVehicleId));
        }

        if (actualPickupAt is { } pickup && actualReturnAt is { } back && back < pickup)
        {
            throw new ArgumentException("The actual return cannot precede the actual pick-up.", nameof(actualReturnAt));
        }

        BookingId = bookingId;
        Kind = kind;
        Rental = rental;
        ActualPickupAt = actualPickupAt?.ToUniversalTime();
        ActualReturnAt = actualReturnAt?.ToUniversalTime();
        AssignedVehicleId = assignedVehicleId;
    }

    public Guid BookingId { get; }

    public OccupancyKind Kind { get; }

    /// <summary>Planned rental: planned pick-up (included) to planned return (excluded), without the buffer.</summary>
    public Period Rental { get; }

    /// <summary>Instant the vehicle was handed over (departure), when it was.</summary>
    public DateTimeOffset? ActualPickupAt { get; }

    /// <summary>Instant the vehicle came back, when it did.</summary>
    public DateTimeOffset? ActualReturnAt { get; }

    /// <summary>Vehicle assigned by an agent, or pinned by the "guaranteed model" option; <see langword="null"/> when not assigned.</summary>
    public Guid? AssignedVehicleId { get; }
}
