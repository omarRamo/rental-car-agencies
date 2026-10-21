using CarRental.Application.Bookings.Contracts;
using CarRental.Domain.Common;

namespace CarRental.Application.Reporting;

/// <summary>
/// What counts as rental revenue (ADR-0045). A file counts once it is firm — <c>Confirmed</c>, <c>InProgress</c>,
/// <c>Returned</c> or <c>Closed</c>: requests, options and files awaiting their payment are tentative (the booking pace
/// shows them apart); refused, expired, cancelled and no-show files never count (a cancellation fee is reported apart,
/// with the cancellations). Its revenue is the HT total of its current quote minus the HT of the daily contribution
/// (collected for the State); VAT and the deposit are never revenue (invariant 8).
/// </summary>
internal static class RevenueRecognition
{
    public static bool Counts(BookingStatusInfo status) =>
        status is BookingStatusInfo.Confirmed or BookingStatusInfo.InProgress or BookingStatusInfo.Returned or BookingStatusInfo.Closed;

    public static bool IsTentative(BookingStatusInfo status) =>
        status is BookingStatusInfo.PendingValidation
            or BookingStatusInfo.InformationRequested
            or BookingStatusInfo.OptionHeld
            or BookingStatusInfo.AwaitingPayment;

    /// <summary>Rental revenue of the current quote: HT total minus the HT of its daily contribution lines.</summary>
    public static Money RentalRevenue(BookingQuoteFactsInfo quote)
    {
        ArgumentNullException.ThrowIfNull(quote);
        return quote.TotalExclVat - quote.DailyContributionExclVat;
    }

    /// <summary>The file was confirmed at some point (its timeline went to <c>Confirmed</c>, or it went beyond it).</summary>
    public static bool WasConfirmed(BookingFactsInfo booking)
    {
        ArgumentNullException.ThrowIfNull(booking);
        return booking.StatusHistory.Any(c => c.To == BookingStatusInfo.Confirmed)
            || booking.Status is BookingStatusInfo.InProgress or BookingStatusInfo.Returned or BookingStatusInfo.Closed or BookingStatusInfo.NoShow;
    }

    /// <summary>The status of the file at <paramref name="instant"/>, from its timeline; <see langword="null"/> before its creation.</summary>
    public static BookingStatusInfo? StatusAt(BookingFactsInfo booking, DateTimeOffset instant)
    {
        ArgumentNullException.ThrowIfNull(booking);
        BookingStatusInfo? status = null;
        foreach (var change in booking.StatusHistory.OrderBy(c => c.AtUtc))
        {
            if (change.AtUtc > instant)
            {
                break;
            }

            status = change.To;
        }

        return status;
    }
}
