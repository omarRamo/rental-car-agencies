using System.Collections.Frozen;
using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Common.Availability;
using CarRental.Application.Common.Caching;
using CarRental.Application.Common.Persistence;
using CarRental.Domain.Channels;
using CarRental.Domain.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CarRental.Application.Channels.Restrictions;

/// <summary>
/// In-memory snapshot of every current quota and stop-sell (a few dozen rows), by category and sales channel, for the
/// availability engine: it reloads each category snapshot with the restrictions of the five channels, so they must be
/// read in microseconds. Dropped after any committed change of a <see cref="ChannelQuota"/> or a <see cref="Channel"/>
/// (ADR-0044 signal), and after <see cref="TimeToLive"/> as a safety net; the availability cache is then told that
/// every category changed, so the next read applies the new restrictions on every channel.
/// </summary>
internal sealed class ChannelRestrictionsCache(IServiceScopeFactory scopeFactory, TimeProvider timeProvider, IAvailabilityChangeNotifier notifier)
    : ICommittedChangesListener, IDisposable
{
    internal static readonly TimeSpan TimeToLive = TimeSpan.FromMinutes(5);

    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private Snapshot? _snapshot;
    private long _version;

    /// <summary>The rules of a category on a sales channel (location-scoped stop-sells included).</summary>
    public async Task<IReadOnlyList<ChannelRestrictionRule>> GetAsync(Guid categoryId, SalesChannel channel, CancellationToken cancellationToken)
    {
        var snapshot = await CurrentAsync(cancellationToken);
        return snapshot.Rules.TryGetValue((categoryId, channel), out var rules) ? rules : [];
    }

    public void OnCommitted(IReadOnlySet<Type> changedEntityTypes)
    {
        ArgumentNullException.ThrowIfNull(changedEntityTypes);
        if (changedEntityTypes.Contains(typeof(ChannelQuota)) || changedEntityTypes.Contains(typeof(Channel)))
        {
            Invalidate();

            // The availability snapshots hold the restrictions of every channel: reload them all (a few categories).
            notifier.AllChanged();
        }
    }

    public void Invalidate()
    {
        Interlocked.Increment(ref _version);
        Volatile.Write(ref _snapshot, null);
    }

    public void Dispose() => _loadGate.Dispose();

    private async Task<Snapshot> CurrentAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _snapshot) is { } current && !current.IsExpired(timeProvider.GetUtcNow()))
        {
            return current;
        }

        await _loadGate.WaitAsync(cancellationToken);
        try
        {
            if (Volatile.Read(ref _snapshot) is { } loadedMeanwhile && !loadedMeanwhile.IsExpired(timeProvider.GetUtcNow()))
            {
                return loadedMeanwhile;
            }

            var version = Interlocked.Read(ref _version);
            var snapshot = new Snapshot(await LoadAsync(cancellationToken), timeProvider.GetUtcNow() + TimeToLive);
            if (Interlocked.Read(ref _version) == version)
            {
                // Not invalidated during the load: later readers may use it.
                Volatile.Write(ref _snapshot, snapshot);
            }

            return snapshot;
        }
        finally
        {
            _loadGate.Release();
        }
    }

    private async Task<FrozenDictionary<(Guid CategoryId, SalesChannel Channel), IReadOnlyList<ChannelRestrictionRule>>> LoadAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        var yesterday = TunisTime.DateOf(timeProvider.GetUtcNow()).AddDays(-1);
        var rows = await ChannelRestrictionReader.Query(db, yesterday).ToListAsync(cancellationToken);
        return rows
            .GroupBy(r => (r.CategoryId, r.SalesChannel))
            .ToFrozenDictionary(g => g.Key, g => (IReadOnlyList<ChannelRestrictionRule>)[.. g.Select(r => r.Rule)]);
    }

    private sealed record Snapshot(FrozenDictionary<(Guid CategoryId, SalesChannel Channel), IReadOnlyList<ChannelRestrictionRule>> Rules, DateTimeOffset ExpiresAt)
    {
        public bool IsExpired(DateTimeOffset now) => now >= ExpiresAt;
    }
}

/// <summary>
/// Reads the restrictions of a category on a sales channel: from <see cref="ChannelRestrictionsCache"/>, or — inside a
/// transaction, such as the availability guard of a writer — fresh from the database with that transaction, so that a
/// decision to write is never taken on a cached value (ADR-0025, ADR-0044).
/// </summary>
internal sealed class ChannelRestrictionReader(IAppDbContext db, IUnitOfWork unitOfWork, ChannelRestrictionsCache cache, TimeProvider timeProvider)
{
    public async Task<IReadOnlyList<ChannelRestrictionRule>> GetAsync(Guid categoryId, SalesChannel channel, CancellationToken cancellationToken)
    {
        if (!unitOfWork.HasActiveTransaction)
        {
            return await cache.GetAsync(categoryId, channel, cancellationToken);
        }

        var yesterday = TunisTime.DateOf(timeProvider.GetUtcNow()).AddDays(-1);
        var rows = await Query(db, yesterday)
            .Where(r => r.CategoryId == categoryId && r.SalesChannel == channel)
            .ToListAsync(cancellationToken);
        return [.. rows.Select(r => r.Rule)];
    }

    /// <summary>The restrictions still running on <paramref name="since"/> or later, of every channel that is not inactive.</summary>
    internal static IQueryable<RestrictionRow> Query(IAppDbContext db, DateOnly since) =>
        from quota in db.Set<ChannelQuota>().AsNoTracking()
        join channel in db.Set<Channel>().AsNoTracking() on quota.ChannelId equals channel.Id
        where quota.EndDate >= since && channel.State != ChannelState.Inactive
        select new RestrictionRow(
            quota.CategoryId,
            quota.SalesChannel,
            new ChannelRestrictionRule(quota.Kind, quota.LocationId, quota.StartDate, quota.EndDate, quota.ReservedVehicles));

    internal sealed record RestrictionRow(Guid CategoryId, SalesChannel SalesChannel, ChannelRestrictionRule Rule);
}

/// <summary>
/// The quotas and stop-sells of the channels, applied by the availability engine to every reader and writer
/// (<see cref="IChannelAvailabilityPolicy"/>, replacing the phase 1 policy): a channel sees
/// <c>max(0, fleet − vehicles kept for the other channels)</c>, and nothing during a stop-sell of the whole category.
/// A stop-sell limited to a pick-up location is applied by the Channels module to its own messages (the engine has no
/// location); an inactive channel restricts nothing.
/// </summary>
internal sealed class ChannelAvailabilityPolicy(ChannelRestrictionReader reader) : IChannelAvailabilityPolicy
{
    public async Task<ChannelRestrictionsInfo> GetRestrictionsAsync(
        Guid categoryId,
        SalesChannel channel,
        Period range,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(range);
        var rules = await reader.GetAsync(categoryId, channel, cancellationToken);
        if (rules.Count == 0)
        {
            return ChannelRestrictionsInfo.None;
        }

        var view = ChannelRestrictionRules.ForEngine(rules, range);
        return view.IsEmpty
            ? ChannelRestrictionsInfo.None
            : new ChannelRestrictionsInfo([.. view.Reserved.Select(r => new ReservedVehiclesInfo(r.Period, r.Vehicles))], view.StopSales);
    }
}
