using System.Globalization;
using System.Text.Json;
using CarRental.Application.Channels.Adapters;
using CarRental.Application.Channels.Core;
using CarRental.Application.Channels.Logging;
using CarRental.Application.Common.Persistence;
using CarRental.Application.Common.Security;
using CarRental.Application.Pricing.Contracts;
using CarRental.Application.Referential.Contracts;
using CarRental.Domain.Channels;
using CarRental.Domain.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CarRental.Application.Channels.Outbound;

/// <summary>What one synchronization run did.</summary>
public sealed record ChannelSyncRunResult(int Enqueued, int Sent, int Failed, int Skipped, int Reminders);

/// <summary>
/// One run of the outbound synchronization (docs/conception/phase-5-canaux.md §3 "Sortant"), by the in-process pump after
/// the debounce window, by the every-minute job, or from the screen: (1) the availability changes recorded since the
/// last run become queued updates for the connected channels; (2) a newly published plan or channel rule becomes a
/// price update (connected channel) or a reminder to replicate on the extranet (manual channel); (3) the due updates
/// are taken (claims: two runs never send the same item), built from fresh data, sent through the adapter, and kept
/// with their payload, or retried with growing delays and finally failed for good.
/// </summary>
/// <remarks>
/// Only the channels of the active agencies whose channels module is on are sent anything (ADR-0067): the work of the
/// others waits for the module. Their availability changes are still queued (one pending update per channel and
/// category at most, coalesced), so that they leave with fresh data as soon as the module is back or the suspension
/// lifted, and their prices are compared again at that time. A run of the pump or the jobs works across agencies and
/// takes the changes recorded for every agency; a run from the screen works for the agency of the operation only and
/// leaves the recorded changes to the pump, which it wakes.
/// </remarks>
public sealed partial class ChannelSyncDispatcher
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IAppDbContext _db;
    private readonly IUserContext _user;
    private readonly IAgencyDirectory _agencies;
    private readonly ChannelSyncSignal _signal;
    private readonly ChannelSyncQueue _queue;
    private readonly ChannelSyncPayloads _payloads;
    private readonly ChannelContextLoader _contexts;
    private readonly ChannelAdapterRegistry _adapters;
    private readonly IPricingQueries _pricing;
    private readonly ChannelMessageLogger _log;
    private readonly TimeProvider _clock;
    private readonly ChannelSyncOptions _options;
    private readonly ILogger<ChannelSyncDispatcher> _logger;

    internal ChannelSyncDispatcher(
        IAppDbContext db,
        IUserContext user,
        IAgencyDirectory agencies,
        ChannelSyncSignal signal,
        ChannelSyncQueue queue,
        ChannelSyncPayloads payloads,
        ChannelContextLoader contexts,
        ChannelAdapterRegistry adapters,
        IPricingQueries pricing,
        ChannelMessageLogger log,
        TimeProvider clock,
        IOptions<ChannelSyncOptions> options,
        ILogger<ChannelSyncDispatcher> logger)
    {
        _db = db;
        _user = user;
        _agencies = agencies;
        _signal = signal;
        _queue = queue;
        _payloads = payloads;
        _contexts = contexts;
        _adapters = adapters;
        _pricing = pricing;
        _log = log;
        _clock = clock;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<ChannelSyncRunResult> RunAsync(CancellationToken cancellationToken)
    {
        var agencies = await OpenAgenciesAsync(cancellationToken);
        ChannelSyncChanges changes;
        if (_user.IsAcrossAgencies)
        {
            changes = _signal.Drain();
        }
        else
        {
            // The recorded changes concern every agency: the pump takes them.
            changes = ChannelSyncChanges.None;
            _signal.Wake();
        }

        // Before the open agencies are looked at: a drained change of an agency without its module must wait in its queue.
        var enqueued = await EnqueueChangesAsync(changes, cancellationToken);
        if (agencies.Count == 0)
        {
            return new ChannelSyncRunResult(enqueued, 0, 0, 0, 0);
        }

        int priceUpdates, reminders;
        try
        {
            (priceUpdates, reminders) = await CheckPricesAsync(agencies, cancellationToken);
        }
        catch (DbUpdateException exception)
        {
            // Another run (from the screen, which does not wait for the pump's lock) recorded the first price check of the
            // same channel meanwhile: this run stops here and the next one compares again on fresh data.
            LogPriceCheckConflict(exception);
            return new ChannelSyncRunResult(enqueued, 0, 0, 0, 0);
        }

        var (sent, failed, skipped) = await SendDueAsync(agencies, cancellationToken);
        var result = new ChannelSyncRunResult(enqueued + priceUpdates, sent, failed, skipped, reminders);
        if (result != new ChannelSyncRunResult(0, 0, 0, 0, 0))
        {
            LogRun(result.Enqueued, result.Sent, result.Failed, result.Skipped, result.Reminders);
        }

        return result;
    }

    /// <summary>Queues a full resynchronization of every connected channel of an agency whose channels module is on (nightly job).</summary>
    public async Task<int> EnqueueFullResyncAsync(string reason, CancellationToken cancellationToken)
    {
        var agencies = await OpenAgenciesAsync(cancellationToken);
        var channels = await _db.Set<Channel>()
            .Where(c => c.State == ChannelState.Connected && agencies.Contains(c.AgencyId))
            .Select(c => new { c.Id, c.AgencyId })
            .ToListAsync(cancellationToken);
        foreach (var channel in channels)
        {
            await _queue.EnqueueAsync(channel.Id, channel.AgencyId, ChannelSyncOperation.FullResync, null, reason, cancellationToken, immediate: true);
            var state = await StateOfAsync(channel.Id, channel.AgencyId, cancellationToken);
            state.FullResyncRequested(_clock.GetUtcNow());
        }

        await _db.SaveChangesAsync(cancellationToken);
        _signal.Wake();
        return channels.Count;
    }

    /// <summary>
    /// The active agencies whose channels module is on, among those the operation works for: every one across agencies,
    /// the agency of the operation otherwise.
    /// </summary>
    private async Task<IReadOnlyList<Guid>> OpenAgenciesAsync(CancellationToken cancellationToken)
    {
        var open = (await _agencies.ListAsync(cancellationToken)).Where(a => a.IsActive && a.HasModule(AgencyModules.Channels)).Select(a => a.Id);
        return _user.IsAcrossAgencies ? [.. open] : [.. open.Where(id => id == _user.AgencyId)];
    }

    /// <summary>
    /// The recorded changes become queued updates of the connected channels concerned, of every agency (only a run across
    /// agencies drains them): those of an agency without its module wait in its queue.
    /// </summary>
    private async Task<int> EnqueueChangesAsync(ChannelSyncChanges changes, CancellationToken cancellationToken)
    {
        if (changes.IsEmpty)
        {
            return 0;
        }

        var count = 0;
        if (changes.All)
        {
            var connected = await _db.Set<Channel>()
                .Where(c => c.State == ChannelState.Connected)
                .Select(c => new { c.Id, c.AgencyId })
                .ToListAsync(cancellationToken);
            foreach (var channel in connected)
            {
                await _queue.EnqueueAsync(channel.Id, channel.AgencyId, ChannelSyncOperation.PushAvailability, null, "availability", cancellationToken, immediate: true);
                count++;
            }
        }
        else
        {
            var categories = changes.Categories.ToList();
            var targets = await (
                    from mapping in _db.Set<ChannelMapping>()
                    join channel in _db.Set<Channel>() on mapping.ChannelId equals channel.Id
                    where channel.State == ChannelState.Connected
                          && mapping.Kind == ChannelMappingKind.Category && mapping.InternalId != null && categories.Contains(mapping.InternalId.Value)
                    select new { mapping.ChannelId, channel.AgencyId, CategoryId = mapping.InternalId!.Value })
                .ToListAsync(cancellationToken);
            foreach (var target in targets)
            {
                await _queue.EnqueueAsync(target.ChannelId, target.AgencyId, ChannelSyncOperation.PushAvailability, target.CategoryId, "availability", cancellationToken, immediate: true);
                count++;
            }
        }

        await _db.SaveChangesAsync(cancellationToken);
        return count;
    }

    /// <summary>A change of the published prices (plan, version or channel rule) of a selling channel since the last run.</summary>
    private async Task<(int Updates, int Reminders)> CheckPricesAsync(IReadOnlyList<Guid> agencies, CancellationToken cancellationToken)
    {
        var channels = await _db.Set<Channel>().AsNoTracking()
            .Where(c => c.State != ChannelState.Inactive && agencies.Contains(c.AgencyId))
            .Select(c => new { c.Id, c.AgencyId, c.SalesChannel, c.State })
            .ToListAsync(cancellationToken);
        var (updates, reminders) = (0, 0);
        foreach (var channel in channels)
        {
            var plan = await _pricing.GetPublishedPlanAsync(channel.AgencyId, null, cancellationToken);
            var own = await _pricing.GetPublishedPlanAsync(channel.AgencyId, channel.SalesChannel, cancellationToken);
            var rule = plan?.ChannelRules.FirstOrDefault(r => r.Channel == channel.SalesChannel);
            var fingerprint = string.Create(
                CultureInfo.InvariantCulture,
                $"{plan?.Id:N}:{plan?.Version}|{own?.Id:N}:{own?.Version}|{rule?.Mode}:{rule?.MarkupPercent}");
            var state = await StateOfAsync(channel.Id, channel.AgencyId, cancellationToken);
            if (!state.ObserveRates(fingerprint, _clock.GetUtcNow()))
            {
                continue;
            }

            if (channel.State == ChannelState.Connected)
            {
                await _queue.EnqueueAsync(channel.Id, channel.AgencyId, ChannelSyncOperation.PushRates, null, "rates", cancellationToken, immediate: true);
                updates++;
            }
            else
            {
                var published = own ?? plan;
                var detail = published is null ? null : string.Create(CultureInfo.InvariantCulture, $"{published.Name} v{published.Version}");
                _db.Set<ChannelReplicationItem>().Add(ChannelReplicationItem.ForRates(channel.Id, channel.AgencyId, detail, new ChannelStamp(_clock.GetUtcNow(), ChannelSyncJob.ActorName)));
                reminders++;
            }
        }

        await _db.SaveChangesAsync(cancellationToken);
        return (updates, reminders);
    }

    private async Task<(int Sent, int Failed, int Skipped)> SendDueAsync(IReadOnlyList<Guid> agencies, CancellationToken cancellationToken)
    {
        var claimed = await ClaimAsync(agencies, cancellationToken);
        var (sent, failed, skipped) = (0, 0, 0);
        var contexts = new Dictionary<Guid, ChannelContext?>();
        foreach (var group in claimed.GroupBy(i => i.CoalesceKey))
        {
            var items = group.OrderBy(i => i.CreatedAtUtc).ToList();
            var item = items[0];
            if (!contexts.TryGetValue(item.ChannelId, out var channel))
            {
                channel = await _contexts.ByIdAsync(item.ChannelId, cancellationToken);
                contexts[item.ChannelId] = channel;
            }

            var now = _clock.GetUtcNow();
            if (channel is null || channel.State != ChannelState.Connected)
            {
                foreach (var dropped in items)
                {
                    dropped.Skip("The channel is no longer connected: a full resynchronization follows its reconnection.", now);
                    skipped++;
                }

                await _db.SaveChangesAsync(cancellationToken);
                continue;
            }

            var outcome = await SendAsync(channel, item, cancellationToken);
            if (outcome.IsSuccess)
            {
                sent++;
            }
            else
            {
                failed++;
            }

            foreach (var twin in items.Skip(1))
            {
                if (outcome.IsSuccess)
                {
                    twin.SentWith(item);
                }
                else
                {
                    twin.Skip("A twin update of the same channel and category carries the retry.", now);
                }

                skipped++;
            }

            await _db.SaveChangesAsync(cancellationToken);
        }

        return (sent, failed, skipped);
    }

    /// <summary>Takes the due items of <paramref name="agencies"/> for this run (optimistic concurrency: an item taken by another run is left to it).</summary>
    private async Task<IReadOnlyList<ChannelSyncItem>> ClaimAsync(IReadOnlyList<Guid> agencies, CancellationToken cancellationToken)
    {
        var now = _clock.GetUtcNow();
        var due = await _db.Set<ChannelSyncItem>()
            .Where(i => agencies.Contains(i.AgencyId) && i.Status == ChannelSyncStatus.Pending && i.NextAttemptAtUtc <= now && (i.ClaimedUntilUtc == null || i.ClaimedUntilUtc <= now))
            .OrderBy(i => i.NextAttemptAtUtc)
            .Take(Math.Max(1, _options.BatchSize))
            .ToListAsync(cancellationToken);
        if (due.Count == 0)
        {
            return due;
        }

        var token = Guid.CreateVersion7();
        var claimed = due.Where(i => i.TryClaim(token, now, now + _options.ClaimLease)).ToList();
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Another run took some of them first: this run leaves them all and the next one retries.
            LogClaimConflict();
            return [];
        }

        return claimed;
    }

    private async Task<Result> SendAsync(ChannelContext channel, ChannelSyncItem item, CancellationToken cancellationToken)
    {
        var started = _clock.GetTimestamp();
        var adapter = _adapters.Find(channel.AdapterKind);
        string? payload = null;
        Result outcome;
        var operation = item.Operation == ChannelSyncOperation.PushRates ? ChannelMessageOperation.PushRates : ChannelMessageOperation.PushAvailability;
        string excerpt;
        try
        {
            if (adapter is null)
            {
                outcome = ChannelsErrors.AdapterNotConfigured;
                excerpt = item.Operation.ToString();
            }
            else
            {
                (outcome, payload, excerpt) = item.Operation switch
                {
                    ChannelSyncOperation.PushAvailability => await PushAvailabilityAsync(adapter, channel, item.CategoryId, cancellationToken),
                    ChannelSyncOperation.PushRates => await PushRatesAsync(adapter, channel, cancellationToken),
                    _ => await FullResyncAsync(adapter, channel, cancellationToken),
                };
            }
        }
#pragma warning disable CA1031 // A failing adapter or read must not stop the run: the item is retried like a refused push.
        catch (Exception exception) when (exception is not OperationCanceledException)
#pragma warning restore CA1031
        {
            // An adapter or a read failed: the item is retried like a refused push.
            LogPushFailed(exception, channel.Code, item.Operation.ToString());
            outcome = Error.Failure(ChannelsErrors.PushFailed.Code, exception.GetType().Name + ": " + exception.Message);
            excerpt = item.Operation.ToString();
        }

        var now = _clock.GetUtcNow();
        if (outcome.IsSuccess)
        {
            item.Succeeded(payload, now);
        }
        else
        {
            item.Failed(string.Create(CultureInfo.InvariantCulture, $"{outcome.Error.Code}: {outcome.Error.Message}"), payload, now, Math.Max(1, _options.MaxAttempts));
        }

        await _log.RecordAsync(new ChannelMessageRecord(
            channel.Id,
            channel.AgencyId,
            channel.Code,
            ChannelMessageDirection.Outbound,
            operation,
            outcome.IsSuccess ? ChannelMessageStatus.Accepted : ChannelMessageStatus.Failed,
            _clock.GetElapsedTime(started),
            now)
        {
            Excerpt = excerpt + (outcome.IsSuccess ? string.Empty : string.Create(CultureInfo.InvariantCulture, $" (attempt {item.Attempts})")),
            ErrorCode = outcome.IsSuccess ? null : outcome.Error.Code,
        }, CancellationToken.None);
        return outcome;
    }

    private async Task<(Result Outcome, string Payload, string Excerpt)> PushAvailabilityAsync(IChannelAdapter adapter, ChannelContext channel, Guid? categoryId, CancellationToken cancellationToken)
    {
        var update = await _payloads.AvailabilityAsync(channel, categoryId, cancellationToken);
        var outcome = await adapter.PushAvailabilityAsync(channel.Target, update, cancellationToken);
        return (outcome, JsonSerializer.Serialize(new { availability = update }, Json), Describe(update));
    }

    private async Task<(Result Outcome, string Payload, string Excerpt)> PushRatesAsync(IChannelAdapter adapter, ChannelContext channel, CancellationToken cancellationToken)
    {
        var update = await _payloads.RatesAsync(channel, cancellationToken);
        var outcome = await adapter.PushRatesAsync(channel.Target, update, cancellationToken);
        return (outcome, JsonSerializer.Serialize(new { rates = update }, Json), Describe(update));
    }

    private async Task<(Result Outcome, string Payload, string Excerpt)> FullResyncAsync(IChannelAdapter adapter, ChannelContext channel, CancellationToken cancellationToken)
    {
        var availability = await _payloads.AvailabilityAsync(channel, null, cancellationToken);
        var rates = await _payloads.RatesAsync(channel, cancellationToken);
        var payload = JsonSerializer.Serialize(new { availability, rates }, Json);
        var excerpt = "full resync: " + Describe(availability) + "; " + Describe(rates);
        var pushed = await adapter.PushAvailabilityAsync(channel.Target, availability, cancellationToken);
        if (pushed.IsFailure)
        {
            return (pushed, payload, excerpt);
        }

        return (await adapter.PushRatesAsync(channel.Target, rates, cancellationToken), payload, excerpt);
    }

    private static string Describe(ChannelAvailabilityUpdate update) => string.Create(
        CultureInfo.InvariantCulture,
        $"availability: {update.Items.Count} item(s), categories {string.Join(',', update.Items.Select(i => i.CategoryCode).Distinct())}");

    private static string Describe(ChannelRatesUpdate update) => string.Create(
        CultureInfo.InvariantCulture,
        $"rates: {update.Items.Count} item(s), categories {string.Join(',', update.Items.Select(i => i.CategoryCode).Distinct())}");

    private async Task<ChannelSyncState> StateOfAsync(Guid channelId, Guid agencyId, CancellationToken cancellationToken)
    {
        var state = await _db.Set<ChannelSyncState>().FirstOrDefaultAsync(s => s.Id == channelId, cancellationToken);
        if (state is null)
        {
            state = ChannelSyncState.For(channelId, agencyId);
            _db.Set<ChannelSyncState>().Add(state);
        }

        return state;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Channel sync run: {Enqueued} queued, {Sent} sent, {Failed} failed, {Skipped} skipped, {Reminders} extranet reminder(s).")]
    private partial void LogRun(int enqueued, int sent, int failed, int skipped, int reminders);

    [LoggerMessage(Level = LogLevel.Information, Message = "Channel sync items were taken by another run; this run leaves them.")]
    private partial void LogClaimConflict();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Channel price check met a concurrent run; this run stops and the next one compares again.")]
    private partial void LogPriceCheckConflict(Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Push {Operation} to channel {ChannelCode} failed; it is retried.")]
    private partial void LogPushFailed(Exception exception, string channelCode, string operation);
}
