using CarRental.Domain.Common;

namespace CarRental.Application.Reporting.Indicators;

/// <summary>
/// Revenue and margin by sales channel (SPEC §8 "CA et marge par canal, après commissions", ADR-0046). Revenue and
/// commission come from the rental shares of the vehicle-days (exact, even when two rentals share a day); the direct
/// costs and depreciation of a rented vehicle-day go to the channel of its main rental, those of a day without rental to
/// no channel (idle costs), so that the channel margins minus the idle costs make the fleet margin.
/// </summary>
internal static class ChannelCalculator
{
    public static ChannelReport Compute(IEnumerable<SnapshotRowFigures> rows, IEnumerable<RentalShareFigures> shares, Money unattributedRevenue)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(shares);
        var shareList = shares.ToList();
        var rowList = rows.ToList();
        var channels = Enum.GetValues<SalesChannel>()
            .Select(channel =>
            {
                var ofChannel = shareList.Where(s => s.Channel == channel).ToList();
                return new ChannelPerformance(
                    channel,
                    ofChannel.Select(s => s.BookingId).Distinct().Count(),
                    ofChannel.Count,
                    ofChannel.Select(s => s.Revenue).Sum(),
                    ofChannel.Select(s => s.Commission).Sum(),
                    rowList.Where(r => r.IsRented && r.Channel == channel).Select(r => r.DirectCosts + r.Depreciation).Sum());
            })
            .ToList();
        var idle = rowList.Where(r => !r.IsRented).Select(r => r.DirectCosts + r.Depreciation).Sum();
        return new ChannelReport(channels, idle, unattributedRevenue);
    }
}
