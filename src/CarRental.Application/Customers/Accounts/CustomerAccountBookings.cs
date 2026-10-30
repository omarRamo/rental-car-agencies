using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Common.Persistence;
using CarRental.Application.Fleet.Contracts;
using CarRental.Domain.Common;
using CarRental.Domain.Customers.Accounts;
using Microsoft.EntityFrameworkCore;

namespace CarRental.Application.Customers.Accounts;

/// <summary>A booking of a customer account: its summary and the ACRISS code of its category.</summary>
public sealed record CustomerBookingItem(BookingSummaryInfo Booking, string CategoryCode);

/// <summary>
/// The bookings a customer account may see (ADR-0070): those made with the account, and those of the person the account
/// is linked to (as a driver or the payer) — a booking made at the counter or by phone shows up in the application once
/// the agency registered the same person. Read through the Bookings contracts, in the agency of the operation.
/// </summary>
public sealed class CustomerAccountBookings(IAppDbContext db, IBookingsQueries bookings, IFleetQueries fleet, TimeProvider clock)
{
    /// <summary>Bookings listed at most (an account books a few times a year).</summary>
    public const int MaxBookings = 200;

    /// <summary>Remembers that <paramref name="bookingId"/> was made with the account, and the person the account books for.</summary>
    public async Task<Result> LinkAsync(Guid accountId, Guid bookingId, Guid? mainDriverPersonId, CancellationToken cancellationToken = default)
    {
        var account = await db.Set<CustomerAccount>().FirstOrDefaultAsync(a => a.Id == accountId, cancellationToken);
        if (account is null)
        {
            return CustomerAccountErrors.NotFound;
        }

        db.Set<CustomerAccountBooking>().Add(CustomerAccountBooking.Link(account.AgencyId, account.Id, bookingId, clock.GetUtcNow()));
        if (mainDriverPersonId is { } personId)
        {
            account.LinkPerson(personId);
        }

        await db.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    /// <summary>Whether the account may see the booking: made with it, or a booking of its person.</summary>
    public async Task<bool> OwnsAsync(Guid accountId, Guid bookingId, CancellationToken cancellationToken = default)
    {
        if (await db.Set<CustomerAccountBooking>().AsNoTracking().AnyAsync(l => l.AccountId == accountId && l.BookingId == bookingId, cancellationToken))
        {
            return true;
        }

        var personId = await db.Set<CustomerAccount>().AsNoTracking().Where(a => a.Id == accountId).Select(a => a.PersonId).FirstOrDefaultAsync(cancellationToken);
        if (personId is null)
        {
            return false;
        }

        var ofPerson = await bookings.GetBookingsOfPersonAsync(personId.Value, cancellationToken);
        return ofPerson.Any(b => b.Booking.Id == bookingId);
    }

    /// <summary>The bookings of the account, most recent pick-up first, drafts excluded.</summary>
    public async Task<IReadOnlyList<CustomerBookingItem>> ListAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        var account = await db.Set<CustomerAccount>().AsNoTracking().Where(a => a.Id == accountId).Select(a => new { a.PersonId }).FirstOrDefaultAsync(cancellationToken);
        if (account is null)
        {
            return [];
        }

        var items = new Dictionary<Guid, CustomerBookingItem>();
        if (account.PersonId is { } personId)
        {
            foreach (var booking in await bookings.GetBookingsOfPersonAsync(personId, cancellationToken))
            {
                items[booking.Booking.Id] = new CustomerBookingItem(booking.Booking, booking.CategoryCode);
            }
        }

        var linked = await db.Set<CustomerAccountBooking>()
            .AsNoTracking()
            .Where(l => l.AccountId == accountId)
            .OrderByDescending(l => l.CreatedAtUtc)
            .Select(l => l.BookingId)
            .Take(MaxBookings)
            .ToListAsync(cancellationToken);
        foreach (var bookingId in linked.Where(id => !items.ContainsKey(id)))
        {
            if (await bookings.GetSummaryAsync(bookingId, cancellationToken) is { } summary && summary.Status != BookingStatusInfo.Draft)
            {
                var category = await fleet.GetCategoryAsync(summary.CategoryId, cancellationToken);
                items[bookingId] = new CustomerBookingItem(summary, category?.AcrissCode ?? string.Empty);
            }
        }

        return [.. items.Values.OrderByDescending(i => i.Booking.PickupAt).Take(MaxBookings)];
    }
}
