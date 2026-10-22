using CarRental.Application.Channels.Contracts;
using CarRental.Application.Common.Persistence;
using CarRental.Domain.Channels;
using CarRental.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace CarRental.Application.Channels.Queries;

/// <summary>
/// <see cref="IChannelPayouts"/>: the monthly settlements of the channels computed by <see cref="ChannelReconciliation"/>,
/// the same figures as the reconciliation screen of the channels.
/// </summary>
internal sealed class ChannelPayoutsQueries(IAppDbContext db) : IChannelPayouts
{
    public async Task<IReadOnlyList<ChannelSettlementInfo>> GetSettlementsAsync(Guid agencyId, int year, int month, CancellationToken cancellationToken = default)
    {
        if (year is < 2000 or > 2100 || month is < 1 or > 12)
        {
            return [];
        }

        var channels = await db.Set<Channel>().AsNoTracking().Where(c => c.AgencyId == agencyId).ToListAsync(cancellationToken);
        var settlements = new List<ChannelSettlementInfo>(channels.Count);
        foreach (var channel in channels.OrderBy(c => c.Name, StringComparer.CurrentCulture))
        {
            var payout = await db.Set<ChannelPayout>().AsNoTracking()
                .FirstOrDefaultAsync(p => p.ChannelId == channel.Id && p.Year == year && p.Month == month, cancellationToken);
            settlements.Add(await SettlementAsync(channel, year, month, payout, cancellationToken));
        }

        return settlements;
    }

    public async Task<IReadOnlyList<ChannelSettlementInfo>> GetRecordedPayoutsAsync(Guid agencyId, DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
    {
        if (to < from)
        {
            return [];
        }

        var start = TunisTime.StartOfDay(from);
        var end = TunisTime.StartOfDay(to.AddDays(1));
        var channels = await db.Set<Channel>().AsNoTracking().Where(c => c.AgencyId == agencyId).ToDictionaryAsync(c => c.Id, cancellationToken);
        if (channels.Count == 0)
        {
            return [];
        }

        var channelIds = channels.Keys.ToList();
        var payouts = await db.Set<ChannelPayout>().AsNoTracking()
            .Where(p => channelIds.Contains(p.ChannelId) && p.RecordedAtUtc >= start && p.RecordedAtUtc < end)
            .ToListAsync(cancellationToken);
        var settlements = new List<ChannelSettlementInfo>(payouts.Count);
        foreach (var payout in payouts.OrderBy(p => p.RecordedAtUtc).ThenBy(p => p.Id))
        {
            settlements.Add(await SettlementAsync(channels[payout.ChannelId], payout.Year, payout.Month, payout, cancellationToken));
        }

        return settlements;
    }

    private async Task<ChannelSettlementInfo> SettlementAsync(Channel channel, int year, int month, ChannelPayout? payout, CancellationToken cancellationToken)
    {
        var first = new DateOnly(year, month, 1);
        var start = TunisTime.StartOfDay(first);
        var end = TunisTime.StartOfDay(first.AddMonths(1));
        var bookings = await db.Set<ChannelBooking>().AsNoTracking()
            .Where(b => b.ChannelId == channel.Id && b.PickupAt >= start && b.PickupAt < end)
            .Select(b => new ChannelBookingFigures(b.State, b.PaymentMode, b.Gross, b.Commission, b.Net))
            .ToListAsync(cancellationToken);
        var figures = ChannelReconciliation.Compute(bookings, payout?.Amount);
        var prepaidGross = bookings
            .Where(b => b.State != ChannelBookingState.Cancelled && b.PaymentMode == ChannelPaymentMode.PrepaidToChannel)
            .Select(b => b.Gross)
            .Sum();
        return new ChannelSettlementInfo(
            channel.Id,
            channel.Code,
            channel.Name,
            channel.SalesChannel,
            year,
            month,
            figures.Bookings,
            figures.Cancelled,
            figures.Gross,
            figures.Commission,
            figures.Net,
            prepaidGross,
            figures.PrepaidNet,
            figures.PayOnSiteCommission,
            figures.ExpectedSettlement,
            payout?.Id,
            payout?.Amount,
            payout?.Reference,
            payout?.RecordedAtUtc);
    }
}
