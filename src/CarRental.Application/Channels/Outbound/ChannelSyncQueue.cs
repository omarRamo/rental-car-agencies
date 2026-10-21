using CarRental.Application.Common.Persistence;
using CarRental.Domain.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CarRental.Application.Channels.Outbound;

/// <summary>
/// Adds outbound updates to the queue of the current unit of work, coalesced: a pending item of the same key (channel,
/// operation, category) absorbs the new trigger and is sent no later than the debounce window. Saved by the caller —
/// an administration service writes its change and the update in the same transaction, so a committed quota is
/// always pushed.
/// </summary>
internal sealed class ChannelSyncQueue(IAppDbContext db, TimeProvider clock, IOptions<ChannelSyncOptions> options)
{
    /// <summary>
    /// Queues an update (or coalesces it into the pending one), due after the debounce window, or at once when
    /// <paramref name="immediate"/> (the pump already waited it). The caller wakes the pump once committed
    /// (<see cref="ChannelSyncSignal.Wake"/>).
    /// </summary>
    public async Task EnqueueAsync(Guid channelId, ChannelSyncOperation operation, Guid? categoryId, string reason, CancellationToken cancellationToken, bool immediate = false)
    {
        var now = clock.GetUtcNow();
        var dueAt = immediate ? now : now + options.Value.Debounce;
        var key = ChannelSyncItem.KeyOf(channelId, operation, categoryId);

        // Twins created in the same transaction, or by two writers at once, are merged when sent (same key).
        var pending = await db.Set<ChannelSyncItem>()
            .Where(i => i.CoalesceKey == key && i.Status == ChannelSyncStatus.Pending && i.Attempts == 0)
            .OrderBy(i => i.NextAttemptAtUtc)
            .FirstOrDefaultAsync(cancellationToken);
        if (pending is not null && !pending.IsClaimed(now))
        {
            pending.Coalesce(dueAt);
        }
        else
        {
            db.Set<ChannelSyncItem>().Add(ChannelSyncItem.Enqueue(channelId, operation, categoryId, reason, dueAt, now));
        }
    }
}
