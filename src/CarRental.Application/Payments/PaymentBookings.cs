using CarRental.Application.Bookings.Contracts;
using CarRental.Domain.Common;
using CarRental.Domain.Payments;

namespace CarRental.Application.Payments;

/// <summary>
/// The rental file money is recorded for (ADR-0067): a payment, a deposit or a payment link only points to a file the
/// operation sees — the agency filter hides the files of the other agencies — and that belongs to the agency the money is
/// recorded for. Anything else is refused as an unknown file (<see cref="PaymentsErrors.BookingNotFound"/>), which never
/// tells that the file exists in another agency.
/// </summary>
internal static class PaymentBookings
{
    public static async Task<Result> EnsureBookingOfAsync(this IBookingsQueries bookings, Guid bookingId, Guid agencyId, CancellationToken cancellationToken)
    {
        if (bookingId == Guid.Empty)
        {
            return PaymentsErrors.BookingRequired;
        }

        return await bookings.GetSummaryAsync(bookingId, cancellationToken) is { } booking && booking.AgencyId == agencyId
            ? Result.Success()
            : PaymentsErrors.BookingNotFound;
    }
}
