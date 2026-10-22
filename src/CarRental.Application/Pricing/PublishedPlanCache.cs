using System.Collections.Concurrent;
using CarRental.Application.Common.Caching;
using CarRental.Application.Common.Persistence;
using CarRental.Application.Common.Security;
using CarRental.Domain.Common;
using CarRental.Domain.Pricing;
using Microsoft.Extensions.DependencyInjection;

namespace CarRental.Application.Pricing;

/// <summary>Where the quote calculator finds the published plans (ADR-0044).</summary>
internal interface IPublishedPlanSource
{
    /// <summary>The published plan of an agency for a channel (<see langword="null"/>: the default plan), with its content.</summary>
    Task<RatePlan?> FindPublishedAsync(Guid agencyId, SalesChannel? channel, CancellationToken cancellationToken);
}

/// <summary>
/// In-memory copies of the published plans, by agency and channel: a quote otherwise loads the plan and its eight child
/// collections, and a results page prices every category (ADR-0044). The copies are detached from any context and only
/// read. Every committed change of a pricing entity drops them all, as does <see cref="TimeToLive"/> (safety net).
/// </summary>
internal sealed class PublishedPlanCache(IServiceScopeFactory scopeFactory, TimeProvider timeProvider) : ICommittedChangesListener, IDisposable
{
    internal static readonly TimeSpan TimeToLive = TimeSpan.FromMinutes(10);

    private static readonly string PricingNamespace = typeof(RatePlan).Namespace!;

    private readonly ConcurrentDictionary<(Guid AgencyId, SalesChannel? Channel), Entry> _entries = new();
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private long _version;

    public async Task<RatePlan?> GetAsync(Guid agencyId, SalesChannel? channel, CancellationToken cancellationToken)
    {
        var key = (agencyId, channel);
        if (_entries.TryGetValue(key, out var cached) && !cached.IsExpired(timeProvider.GetUtcNow()))
        {
            return cached.Plan;
        }

        await _loadGate.WaitAsync(cancellationToken);
        try
        {
            if (_entries.TryGetValue(key, out var loadedMeanwhile) && !loadedMeanwhile.IsExpired(timeProvider.GetUtcNow()))
            {
                return loadedMeanwhile.Plan;
            }

            var version = Interlocked.Read(ref _version);
            RatePlan? plan;
            await using (var scope = scopeFactory.CreateAsyncScope())
            {
                // Read across agencies whoever triggers the load: the copies are shared by every reader (ADR-0067).
                scope.ServiceProvider.GetService<ScopedUserContext>()?.Initialize(UserContextSnapshot.System("system:published-plan-cache"));
                // Loaded tracked by a throw-away context (so that its collections fix up), then detached with it.
                plan = await scope.ServiceProvider.GetRequiredService<IRatePlanStore>().FindPublishedAsync(agencyId, channel, cancellationToken);
            }

            if (Interlocked.Read(ref _version) == version)
            {
                _entries[key] = new Entry(plan, timeProvider.GetUtcNow() + TimeToLive);
            }

            return plan;
        }
        finally
        {
            _loadGate.Release();
        }
    }

    public void OnCommitted(IReadOnlySet<Type> changedEntityTypes)
    {
        ArgumentNullException.ThrowIfNull(changedEntityTypes);
        if (changedEntityTypes.Any(t => string.Equals(t.Namespace, PricingNamespace, StringComparison.Ordinal)))
        {
            Invalidate();
        }
    }

    public void Invalidate()
    {
        Interlocked.Increment(ref _version);
        _entries.Clear();
    }

    public void Dispose() => _loadGate.Dispose();

    private sealed record Entry(RatePlan? Plan, DateTimeOffset ExpiresAt)
    {
        public bool IsExpired(DateTimeOffset now) => now >= ExpiresAt;
    }
}

/// <summary>
/// Published plans for the quote calculator: from the <see cref="PublishedPlanCache"/>, except inside a transaction, where
/// the plan is read with the transaction (a writer sees its own changes; nothing uncommitted is cached).
/// </summary>
internal sealed class CachedPublishedPlanSource(PublishedPlanCache cache, IRatePlanStore store, IUnitOfWork unitOfWork) : IPublishedPlanSource
{
    public Task<RatePlan?> FindPublishedAsync(Guid agencyId, SalesChannel? channel, CancellationToken cancellationToken) =>
        unitOfWork.HasActiveTransaction
            ? store.FindPublishedAsync(agencyId, channel, cancellationToken)
            : cache.GetAsync(agencyId, channel, cancellationToken);
}
