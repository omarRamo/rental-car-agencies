namespace CarRental.Domain.Common;

/// <summary>Channel through which a booking is created or a price is asked (SPEC §4, "Règles par canal").</summary>
public enum SalesChannel
{
    /// <summary>Public website (booking request validated by an agent).</summary>
    Web = 1,

    /// <summary>WhatsApp: the bot only sends a pre-filled link, the request then follows the web flow with this source.</summary>
    WhatsApp = 2,

    /// <summary>Phone: an agent creates a dated option or a confirmed booking.</summary>
    Phone = 3,

    /// <summary>Counter (walk-in): an agent creates the booking and does the departure.</summary>
    Counter = 4,

    /// <summary>Booking.com (or its connectivity provider): bookings arrive confirmed.</summary>
    BookingCom = 5,
}
