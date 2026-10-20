namespace CarRental.Application.Bookings.Availability;

/// <summary>Settings of the in-memory availability cache (ADR-0025); the defaults suit one agency of about 30 vehicles.</summary>
public sealed class AvailabilityCacheOptions
{
    /// <summary>Days before today (Tunis) covered by a snapshot.</summary>
    public int HorizonDaysBefore { get; set; } = 1;

    /// <summary>Days after today (Tunis) covered by a snapshot; a period beyond it is read fresh, without cache.</summary>
    public int HorizonDaysAfter { get; set; } = 400;

    /// <summary>
    /// Safety net: a snapshot is reloaded after this delay even without any change notification (missed notification,
    /// rolling "now" of an overdue rental).
    /// </summary>
    public TimeSpan AbsoluteExpiration { get; set; } = TimeSpan.FromMinutes(2);
}
