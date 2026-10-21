using CarRental.Application.Common.Persistence;
using CarRental.Application.Common.Security;
using CarRental.Application.Identity.Contracts;
using CarRental.Domain.Channels;
using CarRental.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace CarRental.Application.Channels.Administration;

/// <summary>
/// Monthly reconciliation of a channel (SPEC §8 "versements Booking.com contre réservations et commissions", permission
/// <c>channels.manage</c>): the channel bookings of the month (by Tunis pick-up date) with their gross price, commission
/// and net, the engine's price for comparison, what the channel should pay and invoice, the payout entered by hand and
/// the difference. TODO(spec): the statement format of the channel, to import invoices and payouts.
/// </summary>
public sealed class ChannelReconciliationService
{
    private readonly IAppDbContext _db;
    private readonly IUserContext _user;
    private readonly TimeProvider _clock;

    internal ChannelReconciliationService(IAppDbContext db, IUserContext user, TimeProvider clock)
    {
        _db = db;
        _user = user;
        _clock = clock;
    }

    private bool CanManage => _user.HasPermission(Permissions.ChannelsManage);

    public async Task<Result<ReconciliationView>> GetMonthAsync(Guid channelId, int year, int month, CancellationToken cancellationToken = default)
    {
        if (!CanManage)
        {
            return ChannelsErrors.Forbidden;
        }

        if (year is < 2000 or > 2100 || month is < 1 or > 12)
        {
            return ChannelsErrors.PayoutMonthInvalid;
        }

        var channel = await _db.Set<Channel>().AsNoTracking().FirstOrDefaultAsync(c => c.Id == channelId, cancellationToken);
        if (channel is null)
        {
            return ChannelsErrors.NotFound;
        }

        var first = new DateOnly(year, month, 1);
        var start = TunisTime.StartOfDay(first);
        var end = TunisTime.StartOfDay(first.AddMonths(1));
        var bookings = await _db.Set<ChannelBooking>().AsNoTracking()
            .Where(b => b.ChannelId == channelId && b.PickupAt >= start && b.PickupAt < end)
            .OrderBy(b => b.PickupAt)
            .ToListAsync(cancellationToken);
        var payout = await _db.Set<ChannelPayout>().AsNoTracking().FirstOrDefaultAsync(p => p.ChannelId == channelId && p.Year == year && p.Month == month, cancellationToken);
        var figures = ChannelReconciliation.Compute(
            bookings.Select(b => new ChannelBookingFigures(b.State, b.PaymentMode, b.Gross, b.Commission, b.Net)),
            payout?.Amount);
        return new ReconciliationView(
            channel.Id,
            channel.Name,
            year,
            month,
            figures,
            [
                .. bookings.Select(b => new ReconciliationBookingView(
                    b.BookingId,
                    b.BookingNumber,
                    b.ChannelReference,
                    b.PickupAt,
                    b.State,
                    b.PaymentMode,
                    b.Source,
                    b.Gross,
                    b.Commission,
                    b.Net,
                    b.EngineTotalInclVat)),
            ],
            payout?.Reference,
            payout?.Note,
            payout?.RecordedAtUtc,
            payout?.RecordedBy);
    }

    /// <summary>Enters (or corrects) the total paid by the channel for the month.</summary>
    public async Task<Result> RecordPayoutAsync(Guid channelId, int year, int month, decimal amount, string? reference, string? note, CancellationToken cancellationToken = default)
    {
        if (!CanManage)
        {
            return ChannelsErrors.Forbidden;
        }

        var money = Money.FromExact(amount, Currency.Tnd);
        if (money.IsFailure)
        {
            return ChannelsErrors.PayoutAmountInvalid;
        }

        if (!await _db.Set<Channel>().AnyAsync(c => c.Id == channelId, cancellationToken))
        {
            return ChannelsErrors.NotFound;
        }

        var stamp = new ChannelStamp(_clock.GetUtcNow(), _user.UserName);
        var payout = await _db.Set<ChannelPayout>().FirstOrDefaultAsync(p => p.ChannelId == channelId && p.Year == year && p.Month == month, cancellationToken);
        Result outcome;
        if (payout is null)
        {
            var created = ChannelPayout.Record(channelId, year, month, money.Value, reference, note, stamp);
            if (created.IsFailure)
            {
                return created.Error;
            }

            _db.Set<ChannelPayout>().Add(created.Value);
            outcome = Result.Success();
        }
        else
        {
            outcome = payout.Change(money.Value, reference, note, stamp);
        }

        if (outcome.IsFailure)
        {
            return outcome;
        }

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Two payouts of the same month at once (unique index), or a concurrent correction: reload and retry.
            return ChannelsErrors.ConcurrentUpdate;
        }

        return Result.Success();
    }
}
