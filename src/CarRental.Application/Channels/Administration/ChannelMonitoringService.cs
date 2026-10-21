using CarRental.Application.Channels.Outbound;
using CarRental.Application.Common.Paging;
using CarRental.Application.Common.Persistence;
using CarRental.Application.Common.Security;
using CarRental.Application.Fleet.Contracts;
using CarRental.Application.Identity.Contracts;
using CarRental.Domain.Channels;
using CarRental.Domain.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CarRental.Application.Channels.Administration;

/// <summary>
/// Supervision of the channels (permission <c>channels.manage</c>): the message log with filters, the outbound queue
/// with its replay, an immediate synchronization run, and the state behind the health entry <c>channels</c>.
/// </summary>
public sealed class ChannelMonitoringService
{
    private readonly IAppDbContext _db;
    private readonly IUserContext _user;
    private readonly IFleetQueries _fleet;
    private readonly ChannelSyncDispatcher _dispatcher;
    private readonly ChannelSyncSignal _signal;
    private readonly TimeProvider _clock;
    private readonly ChannelSyncOptions _options;

    internal ChannelMonitoringService(
        IAppDbContext db,
        IUserContext user,
        IFleetQueries fleet,
        ChannelSyncDispatcher dispatcher,
        ChannelSyncSignal signal,
        TimeProvider clock,
        IOptions<ChannelSyncOptions> options)
    {
        _db = db;
        _user = user;
        _fleet = fleet;
        _dispatcher = dispatcher;
        _signal = signal;
        _clock = clock;
        _options = options.Value;
    }

    private bool CanManage => _user.HasPermission(Permissions.ChannelsManage);

    /// <summary>The state of the outbound queue: failed for good, pending (and retrying), the oldest due, the pending reminders.</summary>
    public async Task<Result<ChannelSyncStatistics>> GetStatisticsAsync(CancellationToken cancellationToken = default) =>
        CanManage ? await ReadStatisticsAsync(_db, cancellationToken) : ChannelsErrors.Forbidden;

    /// <summary>The figures of the queue (also read by the health entry <c>channels</c>, without permission).</summary>
    public static async Task<ChannelSyncStatistics> ReadStatisticsAsync(IAppDbContext db, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        var failed = await db.Set<ChannelSyncItem>().AsNoTracking().CountAsync(i => i.Status == ChannelSyncStatus.Failed, cancellationToken);
        var pending = await db.Set<ChannelSyncItem>().AsNoTracking()
            .Where(i => i.Status == ChannelSyncStatus.Pending)
            .GroupBy(_ => 1)
            .Select(g => new { Count = g.Count(), Retrying = g.Count(i => i.Attempts > 0), Oldest = g.Min(i => i.NextAttemptAtUtc) })
            .FirstOrDefaultAsync(cancellationToken);
        var reminders = await db.Set<ChannelReplicationItem>().AsNoTracking().CountAsync(r => r.AcknowledgedAtUtc == null, cancellationToken);
        return new ChannelSyncStatistics(failed, pending?.Count ?? 0, pending?.Retrying ?? 0, pending?.Oldest, reminders);
    }

    public async Task<Result<PagedResult<MessageLogEntry>>> ListMessagesAsync(MessageLogFilter filter, PageRequest page, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(page);
        if (!CanManage)
        {
            return ChannelsErrors.Forbidden;
        }

        var query = _db.Set<ChannelMessageLog>().AsNoTracking();
        if (filter.ChannelId is { } channelId)
        {
            query = query.Where(m => m.ChannelId == channelId);
        }

        if (filter.Direction is { } direction)
        {
            query = query.Where(m => m.Direction == direction);
        }

        if (filter.Operation is { } operation)
        {
            query = query.Where(m => m.Operation == operation);
        }

        if (filter.Status is { } status)
        {
            query = query.Where(m => m.Status == status);
        }

        if (!string.IsNullOrWhiteSpace(filter.Reference))
        {
            var reference = filter.Reference.Trim();
            query = query.Where(m => m.ChannelReference != null && m.ChannelReference.Contains(reference));
        }

        if (filter.From is { } from)
        {
            var start = TunisTime.StartOfDay(from);
            query = query.Where(m => m.OccurredAtUtc >= start);
        }

        if (filter.To is { } to)
        {
            var end = TunisTime.StartOfDay(to.AddDays(1));
            query = query.Where(m => m.OccurredAtUtc < end);
        }

        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(m => m.OccurredAtUtc)
            .ThenByDescending(m => m.Id)
            .Skip(page.Skip)
            .Take(page.PageSize)
            .Select(m => new MessageLogEntry(
                m.Id,
                m.OccurredAtUtc,
                m.ChannelCode,
                m.Direction,
                m.Operation,
                m.Status,
                m.ChannelReference,
                m.HttpStatus,
                m.DurationMilliseconds,
                m.ErrorCode,
                m.Excerpt,
                m.RemoteAddress,
                m.BookingId))
            .ToListAsync(cancellationToken);
        return new PagedResult<MessageLogEntry>(items, total, page.Page, page.PageSize);
    }

    public async Task<Result<PagedResult<SyncItemEntry>>> ListSyncItemsAsync(SyncQueueFilter filter, Guid? channelId, PageRequest page, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        if (!CanManage)
        {
            return ChannelsErrors.Forbidden;
        }

        var query =
            from item in _db.Set<ChannelSyncItem>().AsNoTracking()
            join channel in _db.Set<Channel>().AsNoTracking() on item.ChannelId equals channel.Id
            select new { Item = item, channel.Code, channel.AgencyId };
        query = filter switch
        {
            SyncQueueFilter.Pending => query.Where(r => r.Item.Status == ChannelSyncStatus.Pending),
            SyncQueueFilter.Failed => query.Where(r => r.Item.Status == ChannelSyncStatus.Failed),
            SyncQueueFilter.Sent => query.Where(r => r.Item.Status == ChannelSyncStatus.Sent || r.Item.Status == ChannelSyncStatus.Skipped),
            _ => query,
        };
        if (channelId is { } id)
        {
            query = query.Where(r => r.Item.ChannelId == id);
        }

        var total = await query.CountAsync(cancellationToken);
        var rows = await query
            .OrderBy(r => r.Item.Status == ChannelSyncStatus.Failed ? 0 : r.Item.Status == ChannelSyncStatus.Pending ? 1 : 2)
            .ThenByDescending(r => r.Item.CreatedAtUtc)
            .Skip(page.Skip)
            .Take(page.PageSize)
            .ToListAsync(cancellationToken);
        var categories = new Dictionary<Guid, string>();
        foreach (var agencyId in rows.Select(r => r.AgencyId).Distinct())
        {
            foreach (var category in await _fleet.GetCategoriesAsync(agencyId, includeInactive: true, cancellationToken))
            {
                categories[category.Id] = category.AcrissCode;
            }
        }

        var items = rows.Select(r => new SyncItemEntry(
                r.Item.Id,
                r.Item.ChannelId,
                r.Code,
                r.Item.Operation,
                r.Item.CategoryId is { } category ? categories.GetValueOrDefault(category) : null,
                r.Item.Reason,
                r.Item.Status,
                r.Item.Attempts,
                r.Item.CreatedAtUtc,
                r.Item.NextAttemptAtUtc,
                r.Item.SentAtUtc,
                r.Item.LastError,
                r.Item.Payload))
            .ToList();
        return new PagedResult<SyncItemEntry>(items, total, page.Page, page.PageSize);
    }

    /// <summary>Sends an update again now with fresh data (a failed one gets every attempt back).</summary>
    public async Task<Result> ReplayAsync(Guid itemId, CancellationToken cancellationToken = default)
    {
        if (!CanManage)
        {
            return ChannelsErrors.Forbidden;
        }

        var item = await _db.Set<ChannelSyncItem>().FirstOrDefaultAsync(i => i.Id == itemId, cancellationToken);
        if (item is null)
        {
            return ChannelsErrors.SyncItemNotFound;
        }

        item.Replay(_clock.GetUtcNow());
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ChannelsErrors.ConcurrentUpdate;
        }

        _signal.Wake();
        return Result.Success();
    }

    /// <summary>Runs the synchronization now (instead of waiting for the pump or the job), and says what it did.</summary>
    public async Task<Result<ChannelSyncRunResult>> RunNowAsync(CancellationToken cancellationToken = default) =>
        CanManage ? await _dispatcher.RunAsync(cancellationToken) : ChannelsErrors.Forbidden;

    /// <summary>Threshold of the health entry: an update due for longer than this, still unsent, is stale.</summary>
    public TimeSpan StaleAfter => _options.StaleAfter;
}
