namespace CarRental.Domain.Bookings.Availability;

/// <summary>
/// How a booking occupies the capacity of its category, independently of the booking status machine
/// (docs/conception/phase-1-exploitation.md §2 and §3). The Bookings module maps each status onto one kind.
/// </summary>
public enum OccupancyKind
{
    /// <summary>No occupancy: <c>Draft</c> and the terminal statuses (<c>Refused</c>, <c>Expired</c>, <c>Cancelled</c>, <c>NoShow</c>).</summary>
    None = 0,

    /// <summary>
    /// Active, vehicle not handed over yet (<c>PendingValidation</c>, <c>InformationRequested</c>, <c>OptionHeld</c>,
    /// <c>AwaitingPayment</c>, <c>Confirmed</c>): <c>[PickupAt, ReturnAt + buffer)</c>.
    /// </summary>
    Planned = 1,

    /// <summary>
    /// Vehicle out (<c>InProgress</c>): <c>[actual or planned pick-up, max(ReturnAt, now) + buffer)</c>, so that an
    /// overdue vehicle keeps blocking the capacity until it comes back.
    /// </summary>
    InProgress = 2,

    /// <summary>Vehicle back and being prepared (<c>Returned</c>): <c>[actual or planned pick-up, ActualReturnAt + buffer)</c>.</summary>
    Returned = 3,
}
