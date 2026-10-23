using System.Collections.Concurrent;
using CarRental.Application.Common.Availability;

namespace CarRental.Application.Channels.Outbound;

/// <summary>Settings of the outbound synchronization (<c>Channels:Sync</c>).</summary>
public sealed class ChannelSyncOptions
{
    public const string SectionName = "Channels:Sync";

    /// <summary>Changes within this window coalesce into one update per channel and category (debounce).</summary>
    public TimeSpan Debounce { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Attempts of an update before it fails for good (health entry <c>channels</c> degraded).</summary>
    public int MaxAttempts { get; set; } = 8;

    /// <summary>An update due for longer than this, still not sent, makes the health entry <c>channels</c> degraded.</summary>
    public TimeSpan StaleAfter { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>Updates taken by one run.</summary>
    public int BatchSize { get; set; } = 20;

    /// <summary>How long a run keeps the updates it took before another run may take them back.</summary>
    public TimeSpan ClaimLease { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Without a signal, the pump runs this often (due retries); the every-minute job is the safety net.</summary>
    public TimeSpan PumpIdleInterval { get; set; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// In-process record of the availability changes the connected channels must hear about (categories changed, or
/// everything), and the one-permit wake-up of the synchronization pump. Signals coalesce: however many commits happen
/// meanwhile, one run picks them all. The record is not durable: a restart loses at most the debounce window, and
/// the nightly full resynchronization covers it.
/// </summary>
public sealed class ChannelSyncSignal : IDisposable
{
    private readonly ConcurrentDictionary<Guid, byte> _categories = new();
    private readonly SemaphoreSlim _wakeup = new(0, 1);
    private int _all;

    public void CategoryChanged(Guid categoryId)
    {
        _categories.TryAdd(categoryId, 0);
        Wake();
    }

    public void AllChanged()
    {
        Interlocked.Exchange(ref _all, 1);
        Wake();
    }

    /// <summary>Wakes the pump (prices, replays) without recording a change.</summary>
    public void Wake()
    {
        try
        {
            _wakeup.Release();
        }
        catch (SemaphoreFullException)
        {
            // A wake-up is already pending: the next run sees this change too.
        }
    }

    /// <summary>Waits for a signal or for <paramref name="timeout"/>; <see langword="true"/> when signaled.</summary>
    public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken) => _wakeup.WaitAsync(timeout, cancellationToken);

    /// <summary>The changes recorded since the last drain (and forgets them).</summary>
    public ChannelSyncChanges Drain()
    {
        var all = Interlocked.Exchange(ref _all, 0) == 1;
        var categories = new List<Guid>();
        foreach (var categoryId in _categories.Keys)
        {
            if (_categories.TryRemove(categoryId, out _))
            {
                categories.Add(categoryId);
            }
        }

        return new ChannelSyncChanges(all, categories);
    }

    public void Dispose() => _wakeup.Dispose();
}

/// <summary>Availability changes drained from <see cref="ChannelSyncSignal"/>.</summary>
public sealed record ChannelSyncChanges(bool All, IReadOnlyList<Guid> Categories)
{
    /// <summary>No change.</summary>
    public static ChannelSyncChanges None { get; } = new(false, []);

    public bool IsEmpty => !All && Categories.Count == 0;
}

/// <summary>
/// Decorates the application's <see cref="IAvailabilityChangeNotifier"/> (the availability cache): every availability
/// change notified after a commit — bookings, fleet, settings, quotas — is also recorded for the connected channels.
/// </summary>
internal sealed class ChannelAwareAvailabilityNotifier(IAvailabilityChangeNotifier inner, ChannelSyncSignal signal) : IAvailabilityChangeNotifier
{
    public void CategoryChanged(Guid categoryId)
    {
        inner.CategoryChanged(categoryId);
        signal.CategoryChanged(categoryId);
    }

    public void AllChanged()
    {
        inner.AllChanged();
        signal.AllChanged();
    }
}
