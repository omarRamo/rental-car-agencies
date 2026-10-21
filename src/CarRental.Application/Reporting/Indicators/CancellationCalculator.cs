using CarRental.Application.Bookings.Contracts;
using CarRental.Domain.Common;
using CarRental.Domain.Reporting;

namespace CarRental.Application.Reporting.Indicators;

/// <summary>
/// Cancellations and no-shows by channel (SPEC §8 "Annulations et non-présentés : pourcentage par canal", ADR-0046).
/// The cohort is the files due to start in the period (planned pick-up on one of its Tunis days) that were confirmed at
/// some point: the rates are the cancelled ones and the no-shows among them. A request or an option cancelled before any
/// confirmation is counted apart; the fees recorded by the cancellations are summed (VAT included, as recorded), never
/// counted as rental revenue.
/// </summary>
internal static class CancellationCalculator
{
    public static CancellationReport Compute(IEnumerable<BookingFactsInfo> bookings, ReportPeriod period)
    {
        ArgumentNullException.ThrowIfNull(bookings);
        ArgumentNullException.ThrowIfNull(period);
        var cohort = bookings.Where(b => period.Contains(TunisTime.DateOf(b.PickupAt)) && b.Status != BookingStatusInfo.Draft).ToList();
        var channels = Enum.GetValues<SalesChannel>().Select(channel => Row(channel, cohort.Where(b => b.Channel == channel))).ToList();
        return new CancellationReport(channels, Row(null, cohort));
    }

    private static CancellationRow Row(SalesChannel? channel, IEnumerable<BookingFactsInfo> bookings)
    {
        var list = bookings.ToList();
        var firm = list.Where(RevenueRecognition.WasConfirmed).ToList();
        return new CancellationRow(
            channel,
            firm.Count,
            firm.Count(b => b.Status == BookingStatusInfo.Cancelled),
            firm.Count(b => b.Status == BookingStatusInfo.NoShow),
            list.Count(b => b.Status == BookingStatusInfo.Cancelled && !RevenueRecognition.WasConfirmed(b)),
            list.Where(b => b.Status == BookingStatusInfo.Cancelled).Select(b => b.CancellationFee ?? Money.Zero).Sum());
    }
}
