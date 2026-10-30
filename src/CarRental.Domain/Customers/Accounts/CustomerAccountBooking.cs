using CarRental.Domain.Common;

namespace CarRental.Domain.Customers.Accounts;

/// <summary>
/// A booking made with a customer account (ADR-0070): the application lists and follows these bookings, together with
/// those of the person the account is linked to. Identifier of the booking only (the Bookings module owns it).
/// </summary>
public sealed class CustomerAccountBooking : Entity, IAgencyOwned
{
    private CustomerAccountBooking()
    {
    }

    private CustomerAccountBooking(Guid agencyId, Guid accountId, Guid bookingId, DateTimeOffset now)
        : base(NewId())
    {
        AgencyId = agencyId;
        AccountId = accountId;
        BookingId = bookingId;
        CreatedAtUtc = now.ToUniversalTime();
    }

    public Guid AgencyId { get; private set; }

    public Guid AccountId { get; private set; }

    public Guid BookingId { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public static CustomerAccountBooking Link(Guid agencyId, Guid accountId, Guid bookingId, DateTimeOffset now)
    {
        if (agencyId == Guid.Empty || accountId == Guid.Empty || bookingId == Guid.Empty)
        {
            throw new ArgumentException("A link names an agency, an account and a booking.");
        }

        return new CustomerAccountBooking(agencyId, accountId, bookingId, now);
    }
}
