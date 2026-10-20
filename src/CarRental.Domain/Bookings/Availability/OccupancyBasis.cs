using CarRental.Domain.Common;

namespace CarRental.Domain.Bookings.Availability;

/// <summary>
/// The two global inputs of the occupancy rules (docs/conception/phase-1-exploitation.md §3): the current instant —
/// an overdue rental keeps blocking until <c>now + buffer</c> — and the preparation buffer added after every return
/// (setting <c>fleet.preparation_buffer</c> of the agency). A single basis is used for a whole computation, so that
/// the stored bookings and the request are measured the same way.
/// </summary>
public sealed record OccupancyBasis
{
    public OccupancyBasis(DateTimeOffset now, TimeSpan preparationBuffer)
    {
        if (preparationBuffer < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(preparationBuffer), preparationBuffer, "The preparation buffer cannot be negative.");
        }

        Now = now.ToUniversalTime();
        PreparationBuffer = preparationBuffer;
    }

    /// <summary>The current instant (UTC).</summary>
    public DateTimeOffset Now { get; }

    /// <summary>Time needed to prepare a vehicle after a return before it can leave again.</summary>
    public TimeSpan PreparationBuffer { get; }

    /// <summary>Occupancy of a request whose vehicle is not out yet: <c>[start, end + buffer)</c>.</summary>
    public Period OccupancyOf(Period rental)
    {
        ArgumentNullException.ThrowIfNull(rental);
        return rental.ExtendEnd(PreparationBuffer);
    }

    /// <summary>
    /// The occupied period of a booking, buffer included, or <see langword="null"/> when it occupies nothing
    /// (<see cref="OccupancyKind.None"/>, or a vehicle returned at its very start without buffer).
    /// </summary>
    /// <exception cref="ArgumentException">The booking data is inconsistent (a bug of the caller): a returned
    /// booking without actual return, a return before the pick-up, or an actual pick-up after the occupancy end.</exception>
    public Period? PeriodOf(BookingOccupancy booking)
    {
        ArgumentNullException.ThrowIfNull(booking);
        return booking.Kind switch
        {
            OccupancyKind.None => null,
            OccupancyKind.Planned => booking.Rental.ExtendEnd(PreparationBuffer),
            OccupancyKind.InProgress => InProgressPeriod(booking),
            OccupancyKind.Returned => ReturnedPeriod(booking),
            _ => throw new ArgumentOutOfRangeException(nameof(booking), booking.Kind, "Unknown occupancy kind."),
        };
    }

    /// <summary>The occupancy interval of a booking, or <see langword="null"/> when it occupies nothing.</summary>
    public OccupancyInterval? IntervalOf(BookingOccupancy booking) =>
        PeriodOf(booking) is { } period ? new OccupancyInterval(booking.BookingId, period, booking.AssignedVehicleId) : null;

    private Period InProgressPeriod(BookingOccupancy booking)
    {
        var start = booking.ActualPickupAt ?? booking.Rental.Start;
        var vehicleBackAt = booking.Rental.End > Now ? booking.Rental.End : Now;
        var end = vehicleBackAt + PreparationBuffer;
        if (end <= Now)
        {
            // Zero buffer and overdue: the vehicle is still out at this very instant.
            end = Now.AddTicks(1);
        }

        if (end <= start)
        {
            throw new ArgumentException("The actual pick-up of a rental in progress is after the end of its occupancy.", nameof(booking));
        }

        return Period.Create(start, end).Value;
    }

    private Period? ReturnedPeriod(BookingOccupancy booking)
    {
        var start = booking.ActualPickupAt ?? booking.Rental.Start;
        var returnedAt = booking.ActualReturnAt
            ?? throw new ArgumentException("A returned booking needs its actual return instant.", nameof(booking));
        if (returnedAt < start)
        {
            throw new ArgumentException("The actual return of a booking precedes its pick-up.", nameof(booking));
        }

        var end = returnedAt + PreparationBuffer;
        return end > start ? Period.Create(start, end).Value : null;
    }
}
