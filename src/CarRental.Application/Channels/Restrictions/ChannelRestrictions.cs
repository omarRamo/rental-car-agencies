using System.Collections.Frozen;
using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Common.Availability;
using CarRental.Application.Common.Caching;
using CarRental.Application.Common.Persistence;
using CarRental.Application.Common.Security;
using CarRental.Application.Fleet.Contracts;
using CarRental.Domain.Channels;
using CarRental.Domain.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CarRental.Application.Channels.Restrictions;

/// <summary>A restriction rule and the agency of the quota that holds it (the agency of its channel).</summary>
internal sealed record AgencyRestrictionRule(Guid AgencyId, ChannelRestrictionRule Rule);

/// <summary>
/// In-memory snapshot of every current quota and stop-sell (a few dozen rows), by category and sales channel, for the
/// availability engine: it reloads each category snapshot with the restrictions of the five channels, so they must be
/// read in microseconds. Dropped after any committed change of a <see cref="ChannelQuota"/> or a <see cref="Channel"/>
/// (ADR-0044 signal), and after <see cref="TimeToLive"/> as a safety net; the availability cache is then told that
/// every category changed, so the next read applies the new restrictions on every channel.
/// </summary>
/// <remarks>
/// Loaded as the system across agencies (the snapshot is shared by every reader, ADR-0067); each rule keeps the agency of
/// its quota, and <see cref="ChannelRestrictionReader"/> only gives a reader the rules of its agency's category.
/// </remarks>
internal sealed class ChannelRestrictionsCache(IServiceScopeFactory scopeFactory, TimeProvider timeProvider, IAvailabilityChangeNotifier notifier)
    : ICommittedChangesListener, IDisposable
{
    internal static readonly TimeSpan TimeToLive = TimeSpan.FromMinutes(5);

    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private Snapshot? _snapshot;
    private long _version;

    /// <summary>The rules of a category on a sales channel (location-scoped stop-sells included), with the agency of each.</summary>
    public async Task<IReadOnlyList<AgencyRestrictionRule>> GetAsync(Guid categoryId, SalesChannel channel, CancellationToken cancellationToken)
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

    private async Task<FrozenDictionary<(Guid CategoryId, SalesChannel Channel), IReadOnlyList<AgencyRestrictionRule>>> LoadAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();

        // Every agency's restrictions, whoever triggers the load: the snapshot is shared by every reader (ADR-0067).
        scope.ServiceProvider.GetService<ScopedUserContext>()?.Initialize(UserContextSnapshot.System("system:channel-restrictions"));
        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        var yesterday = TunisTime.DateOf(timeProvider.GetUtcNow()).AddDays(-1);
        var rows = await ChannelRestrictionReader.Query(db, yesterday).ToListAsync(cancellationToken);
        return rows
            .GroupBy(r => (r.CategoryId, r.SalesChannel))
            .ToFrozenDictionary(g => g.Key, g => (IReadOnlyList<AgencyRestrictionRule>)[.. g.Select(r => new AgencyRestrictionRule(r.AgencyId, r.Rule))]);
    }

    private sealed record Snapshot(FrozenDictionary<(Guid CategoryId, SalesChannel Channel), IReadOnlyList<AgencyRestrictionRule>> Rules, DateTimeOffset ExpiresAt)
    {
        public bool IsExpired(DateTimeOffset now) => now >= ExpiresAt;
    }
}

/// <summary>
/// Reads the restrictions of a category on a sales channel: from <see cref="ChannelRestrictionsCache"/>, or — inside a
/// transaction, such as the availability guard of a writer — fresh from the database with that transaction, so that a
/// decision to write is never taken on a cached value (ADR-0025, ADR-0044). A reader only ever gets the rules of its
/// agency's category (ADR-0067): the rules held by the quotas of the agency of the category.
/// </summary>
internal sealed class ChannelRestrictionReader(IAppDbContext db, IUnitOfWork unitOfWork, ChannelRestrictionsCache cache, IFleetQueries fleet, TimeProvider timeProvider)
{
    /// <summary>The rules of <paramref name="categoryId"/> of <paramref name="agencyId"/> on a sales channel.</summary>
    public async Task<IReadOnlyList<ChannelRestrictionRule>> GetAsync(Guid agencyId, Guid categoryId, SalesChannel channel, CancellationToken cancellationToken) =>
        [.. (await ReadAsync(categoryId, channel, cancellationToken)).Where(r => r.AgencyId == agencyId).Select(r => r.Rule)];

    /// <summary>
    /// The rules of a category on a sales channel, for a reader that does not know the agency of the category (the
    /// availability engine): those of the category's own agency, asked of the Fleet module only when the category has
    /// rules at all (most have none).
    /// </summary>
    public async Task<IReadOnlyList<ChannelRestrictionRule>> GetOfCategoryAsync(Guid categoryId, SalesChannel channel, CancellationToken cancellationToken)
    {
        var rules = await ReadAsync(categoryId, channel, cancellationToken);
        if (rules.Count == 0 || await fleet.GetCategoryAsync(categoryId, cancellationToken) is not { } category)
        {
            return [];
        }

        return [.. rules.Where(r => r.AgencyId == category.AgencyId).Select(r => r.Rule)];
    }

    /// <summary>
    /// The restrictions still running on <paramref name="since"/> or later, of every channel that is not inactive (only
    /// those of <paramref name="categoryId"/> on <paramref name="salesChannel"/> when given), each with the agency of its
    /// quota — always the agency of its channel. Filters come before the projection, which SQL cannot read back.
    /// </summary>
    internal static IQueryable<RestrictionRow> Query(IAppDbContext db, DateOnly since, Guid? categoryId = null, SalesChannel? salesChannel = null) =>
        from quota in db.Set<ChannelQuota>().AsNoTracking()
        join channel in db.Set<Channel>().AsNoTracking() on quota.ChannelId equals channel.Id
        where quota.EndDate >= since
              && quota.AgencyId == channel.AgencyId
              && channel.State != ChannelState.Inactive
              && (categoryId == null || quota.CategoryId == categoryId)
              && (salesChannel == null || quota.SalesChannel == salesChannel)
        select new RestrictionRow(
            quota.AgencyId,
            quota.CategoryId,
            quota.SalesChannel,
            new ChannelRestrictionRule(quota.Kind, quota.LocationId, quota.StartDate, quota.EndDate, quota.ReservedVehicles));

    private async Task<IReadOnlyList<AgencyRestrictionRule>> ReadAsync(Guid categoryId, SalesChannel channel, CancellationToken cancellationToken)
    {
        if (!unitOfWork.HasActiveTransaction)
        {
            return await cache.GetAsync(categoryId, channel, cancellationToken);
        }

        var yesterday = TunisTime.DateOf(timeProvider.GetUtcNow()).AddDays(-1);
        var rows = await Query(db, yesterday, categoryId, channel).ToListAsync(cancellationToken);
        return [.. rows.Select(r => new AgencyRestrictionRule(r.AgencyId, r.Rule))];
    }

    internal sealed record RestrictionRow(Guid AgencyId, Guid CategoryId, SalesChannel SalesChannel, ChannelRestrictionRule Rule);
}

/// <summary>
/// The quotas and stop-sells of the channels, applied by the availability engine to every reader and writer
/// (<see cref="IChannelAvailabilityPolicy"/>, replacing the phase 1 policy): a channel sees
/// <c>max(0, fleet − vehicles kept for the other channels)</c>, and nothing during a stop-sell of the whole category.
/// A stop-sell limited to a pick-up location is applied by the Channels module to its own messages (the engine has no
/// location); an inactive channel restricts nothing; only the restrictions of the agency of the category count.
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
        var rules = await reader.GetOfCategoryAsync(categoryId, channel, cancellationToken);
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
