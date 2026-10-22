using System.Diagnostics;
using CarRental.Application.Channels.Adapters;
using CarRental.Application.Channels.Core;
using CarRental.Application.Channels.Logging;
using CarRental.Application.Common.Persistence;
using CarRental.Application.Common.Security;
using CarRental.Application.Fleet.Contracts;
using CarRental.Application.Identity.Contracts;
using CarRental.Application.Pricing.Contracts;
using CarRental.Application.Referential.Contracts;
using CarRental.Domain.Channels;
using CarRental.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace CarRental.Application.Channels.Administration;

/// <summary>
/// The manual adapter's back office (docs/conception/phase-5-canaux.md §2): a booking sold on the extranet of a channel
/// (Booking.com) is entered by hand — reference, places, instants, category, main driver, gross price, commission (the
/// rate of the channel when left empty) and payment mode — and becomes a confirmed booking through the same core as an
/// inbound message, under the availability lock (only the physical capacity counts: the channel already sold it), with
/// the same idempotency. Then the "to replicate on the extranet" list: quota, stop-sell and price changes of the manual
/// channels until someone acknowledges them.
/// </summary>
public sealed class ChannelManualEntryService
{
    private readonly IAppDbContext _db;
    private readonly IUserContext _user;
    private readonly ChannelContextLoader _contexts;
    private readonly ChannelReservationCore _core;
    private readonly IFleetQueries _fleet;
    private readonly IReferentialQueries _referential;
    private readonly IPricingService _pricing;
    private readonly ChannelMessageLogger _log;
    private readonly TimeProvider _clock;

    internal ChannelManualEntryService(
        IAppDbContext db,
        IUserContext user,
        ChannelContextLoader contexts,
        ChannelReservationCore core,
        IFleetQueries fleet,
        IReferentialQueries referential,
        IPricingService pricing,
        ChannelMessageLogger log,
        TimeProvider clock)
    {
        _db = db;
        _user = user;
        _contexts = contexts;
        _core = core;
        _fleet = fleet;
        _referential = referential;
        _pricing = pricing;
        _log = log;
        _clock = clock;
    }

    private bool CanManage => _user.HasPermission(Permissions.ChannelsManage);

    /// <summary>The channels that sell (manual or connected) with the categories and locations of their agency.</summary>
    public async Task<Result<IReadOnlyList<ManualEntryChannelView>>> GetChannelsAsync(CancellationToken cancellationToken = default)
    {
        if (!CanManage)
        {
            return ChannelsErrors.Forbidden;
        }

        var channels = await _db.Set<Channel>().AsNoTracking().Where(c => c.State != ChannelState.Inactive).OrderBy(c => c.Name).ToListAsync(cancellationToken);
        var views = new List<ManualEntryChannelView>();
        foreach (var channel in channels)
        {
            var categories = await _fleet.GetCategoriesAsync(channel.AgencyId, includeInactive: false, cancellationToken);
            var locations = await _referential.GetLocationsAsync(channel.AgencyId, cancellationToken);
            views.Add(new ManualEntryChannelView(
                channel.Id,
                channel.Name,
                channel.Code,
                channel.AgencyId,
                channel.State,
                channel.DefaultPaymentMode,
                channel.CommissionRate,
                [.. categories.Select(c => new ManualEntryOptionView(c.Id, c.AcrissCode, c.Name))],
                [.. locations.Select(l => new ManualEntryOptionView(l.Id, l.Code, l.Name))]));
        }

        return views;
    }

    /// <summary>The commission and net the entry would record, and the engine's price for the channel (for comparison).</summary>
    public async Task<Result<ManualEntryPreview>> PreviewAsync(ManualEntryRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!CanManage)
        {
            return ChannelsErrors.Forbidden;
        }

        var channel = await _db.Set<Channel>().AsNoTracking().FirstOrDefaultAsync(c => c.Id == request.ChannelId, cancellationToken);
        if (channel is null)
        {
            return ChannelsErrors.NotFound;
        }

        // Priced for the agency of the channel, with its own category only.
        var category = await _fleet.GetCategoryAsync(request.CategoryId, cancellationToken);
        if (category is null || category.AgencyId != channel.AgencyId)
        {
            return ChannelsErrors.RestrictionCategoryNotFound;
        }

        var amounts = Amounts(request, channel.CommissionRate);
        if (amounts.IsFailure)
        {
            return amounts.Error;
        }

        Money? engine = null;
        string? pricingError = null;
        if (request.ReturnAt > request.PickupAt)
        {
            var quote = await _pricing.QuoteAsync(
                new PriceRequest(channel.AgencyId, request.CategoryId, request.PickupLocationId, request.ReturnLocationId, request.PickupAt, request.ReturnAt, channel.SalesChannel),
                cancellationToken);
            if (quote.IsSuccess)
            {
                engine = quote.Value.TotalInclVat;
            }
            else
            {
                pricingError = quote.Error.Code;
            }
        }

        return new ManualEntryPreview(amounts.Value.Gross, amounts.Value.Commission, amounts.Value.Net, amounts.Value.Source, engine, pricingError);
    }

    /// <summary>Records the booking (confirmed at once, in the planning); entering the same booking again returns it.</summary>
    public async Task<Result<ManualEntryResult>> RecordAsync(ManualEntryRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!CanManage)
        {
            return ChannelsErrors.Forbidden;
        }

        var started = _clock.GetTimestamp();
        var context = await _contexts.ByIdAsync(request.ChannelId, cancellationToken);
        if (context is null)
        {
            return ChannelsErrors.NotFound;
        }

        var reservation = new ChannelReservation(
            request.ChannelReference?.Trim() ?? string.Empty,
            context.Mappings.CodeOfCategory(request.CategoryId) ?? string.Empty,
            context.Mappings.CodeOfLocation(request.PickupLocationId) ?? string.Empty,
            context.Mappings.CodeOfLocation(request.ReturnLocationId) ?? string.Empty,
            request.PickupAt,
            request.ReturnAt,
            new ChannelDriver(request.DriverFirstName ?? string.Empty, request.DriverLastName ?? string.Empty, request.DriverPhone, request.DriverEmail),
            ChannelAmount.Tnd(request.Gross),
            request.Commission is { } commission ? ChannelAmount.Tnd(commission) : null,
            PaymentMode: request.PaymentMode switch
            {
                ChannelPaymentMode.PrepaidToChannel => ChannelReservationPaymentMode.PrepaidToChannel,
                ChannelPaymentMode.PayOnSite => ChannelReservationPaymentMode.PayOnSite,
                _ => null,
            });
        var targets = await CheckTargetsAsync(context, request, cancellationToken);
        var outcome = targets.IsFailure
            ? ChannelInboundOutcome.Refusal(targets.Error)
            : await _core.CreateMappedAsync(
                context,
                reservation,
                new MappedReservation(request.CategoryId, request.PickupLocationId, request.ReturnLocationId, []),
                new ChannelEntryOptions(ChannelBookingSource.Manual, IgnoreChannelRestrictions: true, Notes: request.Notes),
                cancellationToken);

        await _log.RecordAsync(
            new ChannelMessageRecord(
                context.Id,
                context.AgencyId,
                context.Code,
                ChannelMessageDirection.Inbound,
                ChannelMessageOperation.ManualEntry,
                outcome.IsSuccess ? ChannelMessageStatus.Accepted : ChannelMessageStatus.Refused,
                _clock.GetElapsedTime(started),
                _clock.GetUtcNow())
            {
                ChannelReference = reservation.ChannelReference,
                Excerpt = "manual " + ChannelExcerpts.Of(reservation) + (ChannelExcerpts.Of(outcome) is { } summary ? " → " + summary : string.Empty),
                ErrorCode = outcome.Error?.Code,
                CorrelationId = _user.CorrelationId ?? Activity.Current?.TraceId.ToString(),
                RemoteAddress = _user.IpAddress,
                BookingId = outcome.BookingId,
            },
            cancellationToken);

        if (outcome.Error is { } error)
        {
            return error;
        }

        var answer = (ChannelReservationAnswer)outcome.Response!;
        return new ManualEntryResult(outcome.BookingId!.Value, answer.Reservation.BookingNumber, answer.Replayed, Money.Tnd(answer.Reservation.EngineTotalInclVat), Money.Tnd(answer.Reservation.Gross));
    }

    /// <summary>The changes to replicate on the extranets (pending first, then the acknowledged ones of the last 30 days).</summary>
    public async Task<Result<IReadOnlyList<ReplicationView>>> GetRemindersAsync(Guid? channelId = null, bool includeAcknowledged = false, CancellationToken cancellationToken = default)
    {
        if (!CanManage)
        {
            return ChannelsErrors.Forbidden;
        }

        var since = _clock.GetUtcNow().AddDays(-30);
        var rows = await (
                from item in _db.Set<ChannelReplicationItem>().AsNoTracking()
                join channel in _db.Set<Channel>().AsNoTracking() on item.ChannelId equals channel.Id
                where (channelId == null || item.ChannelId == channelId)
                      && (item.AcknowledgedAtUtc == null || (includeAcknowledged && item.AcknowledgedAtUtc >= since))
                orderby item.AcknowledgedAtUtc != null, item.CreatedAtUtc
                select new { Item = item, channel.Name, channel.AgencyId })
            .Take(500)
            .ToListAsync(cancellationToken);
        var codes = new Dictionary<Guid, (IReadOnlyDictionary<Guid, string> Categories, IReadOnlyDictionary<Guid, string> Locations)>();
        foreach (var agencyId in rows.Select(r => r.AgencyId).Distinct())
        {
            codes[agencyId] = (
                (await _fleet.GetCategoriesAsync(agencyId, includeInactive: true, cancellationToken)).ToDictionary(c => c.Id, c => c.AcrissCode),
                (await _referential.GetLocationsAsync(agencyId, cancellationToken)).ToDictionary(l => l.Id, l => l.Code));
        }

        return rows.Select(r => new ReplicationView(
                r.Item.Id,
                r.Item.ChannelId,
                r.Name,
                r.Item.Kind,
                r.Item.CategoryId is { } category ? codes[r.AgencyId].Categories.GetValueOrDefault(category) : null,
                r.Item.LocationId is { } location ? codes[r.AgencyId].Locations.GetValueOrDefault(location) : null,
                r.Item.StartDate,
                r.Item.EndDate,
                r.Item.Vehicles,
                r.Item.Detail,
                r.Item.CreatedAtUtc,
                r.Item.CreatedBy,
                r.Item.AcknowledgedAtUtc,
                r.Item.AcknowledgedBy))
            .ToList();
    }

    /// <summary>Marks changes as replicated on the extranet (all pending ones of the channel when <paramref name="ids"/> is empty).</summary>
    public async Task<Result<int>> AcknowledgeAsync(Guid? channelId, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (!CanManage)
        {
            return ChannelsErrors.Forbidden;
        }

        var query = _db.Set<ChannelReplicationItem>().Where(i => i.AcknowledgedAtUtc == null);
        query = ids.Count > 0 ? query.Where(i => ids.Contains(i.Id)) : query.Where(i => channelId == null || i.ChannelId == channelId);
        var items = await query.ToListAsync(cancellationToken);
        if (ids.Count > 0 && items.Count == 0)
        {
            return ChannelsErrors.ReplicationNotFound;
        }

        var stamp = new ChannelStamp(_clock.GetUtcNow(), _user.UserName);
        var count = items.Count(item => item.Acknowledge(stamp));
        await _db.SaveChangesAsync(cancellationToken);
        return count;
    }

    private static Result<ChannelAmounts> Amounts(ManualEntryRequest request, decimal rate)
    {
        var gross = Money.FromExact(request.Gross, Currency.Tnd);
        if (gross.IsFailure)
        {
            return ChannelsErrors.AmountInvalid;
        }

        Money? commission = null;
        if (request.Commission is { } given)
        {
            var exact = Money.FromExact(given, Currency.Tnd);
            if (exact.IsFailure)
            {
                return ChannelsErrors.AmountInvalid;
            }

            commission = exact.Value;
        }

        return ChannelCommissionRule.Compute(gross.Value, commission, null, rate);
    }

    private async Task<Result> CheckTargetsAsync(ChannelContext channel, ManualEntryRequest request, CancellationToken cancellationToken)
    {
        var category = await _fleet.GetCategoryAsync(request.CategoryId, cancellationToken);
        if (category is null || category.AgencyId != channel.AgencyId || !category.IsActive)
        {
            return ChannelsErrors.RestrictionCategoryNotFound;
        }

        var locations = await _referential.GetLocationsAsync(channel.AgencyId, cancellationToken);
        return locations.Any(l => l.Id == request.PickupLocationId) && locations.Any(l => l.Id == request.ReturnLocationId)
            ? Result.Success()
            : ChannelsErrors.RestrictionLocationNotFound;
    }
}
