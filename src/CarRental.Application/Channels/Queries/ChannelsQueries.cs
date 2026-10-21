using CarRental.Application.Channels.Contracts;
using CarRental.Application.Common.Persistence;
using CarRental.Domain.Channels;
using CarRental.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace CarRental.Application.Channels.Queries;

/// <inheritdoc cref="IChannelsQueries"/>
internal sealed class ChannelsQueries(IAppDbContext db) : IChannelsQueries
{
    public async Task<IReadOnlyList<ChannelSummaryInfo>> GetChannelsAsync(Guid? agencyId = null, CancellationToken cancellationToken = default) =>
        await db.Set<Channel>().AsNoTracking()
            .Where(c => agencyId == null || c.AgencyId == agencyId)
            .OrderBy(c => c.Name)
            .Select(c => new ChannelSummaryInfo(
                c.Id,
                c.AgencyId,
                c.Code,
                c.Name,
                c.SalesChannel,
                (ChannelStateInfo)c.State,
                (ChannelAdapterKindInfo)c.AdapterKind,
                (ChannelPaymentModeInfo)c.DefaultPaymentMode,
                c.CommissionRate))
            .ToListAsync(cancellationToken);

    public async Task<ChannelBookingInfo?> GetChannelBookingAsync(Guid bookingId, CancellationToken cancellationToken = default) =>
        await (
                from booking in db.Set<ChannelBooking>().AsNoTracking()
                join channel in db.Set<Channel>().AsNoTracking() on booking.ChannelId equals channel.Id
                where booking.BookingId == bookingId
                select new ChannelBookingInfo(
                    booking.BookingId,
                    booking.BookingNumber,
                    channel.Id,
                    channel.Code,
                    channel.Name,
                    booking.SalesChannel,
                    booking.ChannelReference,
                    (ChannelBookingSourceInfo)booking.Source,
                    (ChannelBookingStateInfo)booking.State,
                    (ChannelPaymentModeInfo)booking.PaymentMode,
                    booking.Gross,
                    booking.Commission,
                    booking.Net,
                    (ChannelCommissionSourceInfo)booking.CommissionSource,
                    booking.EngineTotalInclVat,
                    booking.ReceivedAtUtc))
            .FirstOrDefaultAsync(cancellationToken);
}

/// <inheritdoc cref="IChannelCommissions"/>
internal sealed class ChannelCommissions(IAppDbContext db) : IChannelCommissions
{
    public async Task<IReadOnlyList<ChannelCommissionInfo>> GetCommissionsAsync(
        DateOnly from,
        DateOnly to,
        Guid? agencyId = null,
        CancellationToken cancellationToken = default)
    {
        if (to < from)
        {
            return [];
        }

        var start = TunisTime.StartOfDay(from);
        var end = TunisTime.StartOfDay(to.AddDays(1));
        return await (
                from booking in db.Set<ChannelBooking>().AsNoTracking()
                join channel in db.Set<Channel>().AsNoTracking() on booking.ChannelId equals channel.Id
                where booking.PickupAt >= start && booking.PickupAt < end && (agencyId == null || booking.AgencyId == agencyId)
                orderby booking.PickupAt, booking.BookingNumber
                select new ChannelCommissionInfo(
                    booking.BookingId,
                    booking.BookingNumber,
                    booking.AgencyId,
                    channel.Id,
                    channel.Code,
                    booking.SalesChannel,
                    booking.ChannelReference,
                    booking.PickupAt,
                    booking.ReturnAt,
                    (ChannelPaymentModeInfo)booking.PaymentMode,
                    (ChannelBookingStateInfo)booking.State,
                    booking.Gross,
                    booking.Commission,
                    booking.Net))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyDictionary<Guid, Money>> GetCommissionsByBookingAsync(
        IReadOnlyCollection<Guid> bookingIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bookingIds);
        if (bookingIds.Count == 0)
        {
            return new Dictionary<Guid, Money>();
        }

        var result = new Dictionary<Guid, Money>();

        // Chunks keep the SQL parameter list short.
        foreach (var chunk in bookingIds.Distinct().Chunk(500))
        {
            var rows = await db.Set<ChannelBooking>().AsNoTracking()
                .Where(b => chunk.Contains(b.BookingId))
                .Select(b => new { b.BookingId, b.Commission })
                .ToListAsync(cancellationToken);
            foreach (var row in rows)
            {
                result[row.BookingId] = row.Commission;
            }
        }

        return result;
    }
}
