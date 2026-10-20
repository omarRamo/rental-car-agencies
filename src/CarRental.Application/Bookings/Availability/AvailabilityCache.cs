using System.Collections.Concurrent;
using CarRental.Application.Common.Availability;
using CarRental.Application.Common.Security;
using CarRental.Application.Fleet.Contracts;
using CarRental.Domain.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CarRental.Application.Bookings.Availability;

/// <summary>
/// In-memory availability snapshot per category — capacity, occupancy and channel restrictions over a rolling horizon
/// (yesterday to today + 400 days by default) — and the category list of each agency, for the fast read path
/// (<c>IAvailabilityQueries</c>: public search, bot, channels). It is the application's
/// <see cref="IAvailabilityChangeNotifier"/>: a change drops the snapshot and the next read reloads it.
/// </summary>
/// <remarks>
/// <para>Single flight: concurrent readers of a cold or expired category share one load (no stampede), and a reader
/// that gives up does not cancel it. A failed load is not kept. A notification removes the entry, so a load started
/// before the notified commit is never served to the readers that come after it.</para>
/// <para>Each load runs in its own DI scope (its own <c>DbContext</c>) as the actor <c>system:availability-cache</c>;
/// snapshots are immutable and shared by every reader. The writers' authoritative check never uses this cache.</para>
/// </remarks>
internal sealed class AvailabilityCache : IAvailabilityChangeNotifier
{
    internal const string ActorName = "system:availability-cache";

    private static readonly SalesChannel[] AllChannels = Enum.GetValues<SalesChannel>();

    private readonly ConcurrentDictionary<Guid, CacheEntry<CategorySnapshot?>> _snapshots = new();
    private readonly ConcurrentDictionary<Guid, CacheEntry<IReadOnlyList<VehicleCategoryInfo>>> _agencyCategories = new();
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _timeProvider;
    private readonly AvailabilityCacheOptions _options;
    private readonly ILogger<AvailabilityCache> _logger;
    private long _snapshotLoads;
    private long _categoryListLoads;

    public AvailabilityCache(
        IServiceScopeFactory scopeFactory,
        TimeProvider timeProvider,
        IOptions<AvailabilityCacheOptions> options,
        ILogger<AvailabilityCache> logger)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        var settings = options.Value;
        if (settings.HorizonDaysBefore < 0 || settings.HorizonDaysAfter < 1 || settings.AbsoluteExpiration <= TimeSpan.Zero)
        {
            throw new ArgumentException(
                "The availability cache needs HorizonDaysBefore >= 0, HorizonDaysAfter >= 1 and a positive AbsoluteExpiration.",
                nameof(options));
        }

        _scopeFactory = scopeFactory;
        _timeProvider = timeProvider;
        _options = settings;
        _logger = logger;
    }

    /// <summary>Snapshot loads started so far (diagnostics and tests).</summary>
    public long SnapshotLoads => Interlocked.Read(ref _snapshotLoads);

    /// <summary>Category list loads started so far (diagnostics and tests).</summary>
    public long CategoryListLoads => Interlocked.Read(ref _categoryListLoads);

    /// <summary>The cached snapshot of a category (loading it when needed), or <see langword="null"/> when the category does not exist.</summary>
    public async Task<CategorySnapshot?> GetSnapshotAsync(Guid categoryId, CancellationToken cancellationToken)
    {
        var snapshot = await GetOrLoadAsync(_snapshots, categoryId, LoadSnapshotAsync, cancellationToken);
        if (snapshot is null)
        {
            // Unknown ids are not kept, so arbitrary ids cannot grow the cache (dropping an entry only costs a reload).
            _snapshots.TryRemove(categoryId, out _);
        }

        return snapshot;
    }

    /// <summary>The active categories of an agency, in display order.</summary>
    public async Task<IReadOnlyList<VehicleCategoryInfo>> GetAgencyCategoriesAsync(Guid agencyId, CancellationToken cancellationToken)
    {
        var categories = await GetOrLoadAsync(_agencyCategories, agencyId, LoadAgencyCategoriesAsync, cancellationToken);
        if (categories.Count == 0)
        {
            _agencyCategories.TryRemove(agencyId, out _);
        }

        return categories;
    }

    /// <summary>A fresh snapshot over <paramref name="coverage"/>, not kept (periods beyond the cached horizon).</summary>
    public async Task<CategorySnapshot?> LoadUncachedAsync(
        Guid categoryId,
        Period coverage,
        SalesChannel? channel,
        CancellationToken cancellationToken)
    {
        await using var scope = CreateScope();
        var loader = scope.ServiceProvider.GetRequiredService<AvailabilitySnapshotLoader>();
        var context = await loader.LoadContextAsync(categoryId, cancellationToken);
        if (context is null)
        {
            return null;
        }

        IReadOnlyCollection<SalesChannel> channels = channel is { } value ? [value] : [];
        return await loader.LoadSnapshotAsync(context, coverage, excludedBookingId: null, channels, cancellationToken);
    }

    public void CategoryChanged(Guid categoryId)
    {
        _snapshots.TryRemove(categoryId, out _);

        // A category change may be a new, renamed or deactivated category: category lists reload too (one query each).
        _agencyCategories.Clear();
        _logger.LogDebug("Availability of category {CategoryId} changed: snapshot dropped.", categoryId);
    }

    public void AllChanged()
    {
        _snapshots.Clear();
        _agencyCategories.Clear();
        _logger.LogDebug("Availability changed for every category: snapshots dropped.");
    }

    private Task<T> GetOrLoadAsync<T>(
        ConcurrentDictionary<Guid, CacheEntry<T>> entries,
        Guid key,
        Func<Guid, Task<T>> load,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var now = _timeProvider.GetUtcNow();
            var entry = entries.GetOrAdd(key, static (id, state) => new CacheEntry<T>(() => state.Load(id), state.Now), (Load: load, Now: now));
            if (!entry.IsStale(now, _options.AbsoluteExpiration))
            {
                return entry.Value.WaitAsync(cancellationToken);
            }

            // Expired or failed: exactly one reader swaps in a new entry, the others pick it up on the next turn.
            var replacement = new CacheEntry<T>(() => load(key), now);
            if (entries.TryUpdate(key, replacement, entry))
            {
                return replacement.Value.WaitAsync(cancellationToken);
            }
        }
    }

    private async Task<CategorySnapshot?> LoadSnapshotAsync(Guid categoryId)
    {
        Interlocked.Increment(ref _snapshotLoads);
        var started = _timeProvider.GetTimestamp();
        try
        {
            await using var scope = CreateScope();
            var loader = scope.ServiceProvider.GetRequiredService<AvailabilitySnapshotLoader>();
            var context = await loader.LoadContextAsync(categoryId, CancellationToken.None);
            if (context is null)
            {
                return null;
            }

            var snapshot = await loader.LoadSnapshotAsync(context, Horizon(context.Basis.Now), excludedBookingId: null, AllChannels, CancellationToken.None);
            _logger.LogDebug(
                "Availability snapshot of category {CategoryId} loaded in {ElapsedMilliseconds} ms: {VehicleCount} vehicles, {IntervalCount} occupancy intervals.",
                categoryId,
                _timeProvider.GetElapsedTime(started).TotalMilliseconds,
                snapshot.Vehicles.Count,
                snapshot.IntervalCount);
            return snapshot;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Loading the availability snapshot of category {CategoryId} failed; the next read retries.", categoryId);
            throw;
        }
    }

    private async Task<IReadOnlyList<VehicleCategoryInfo>> LoadAgencyCategoriesAsync(Guid agencyId)
    {
        Interlocked.Increment(ref _categoryListLoads);
        await using var scope = CreateScope();
        var fleet = scope.ServiceProvider.GetRequiredService<IFleetQueries>();
        return await fleet.GetCategoriesAsync(agencyId, includeInactive: false, CancellationToken.None);
    }

    private AsyncServiceScope CreateScope()
    {
        var scope = _scopeFactory.CreateAsyncScope();
        scope.ServiceProvider.GetService<ScopedUserContext>()?.Initialize(UserContextSnapshot.System(ActorName));
        return scope;
    }

    private Period Horizon(DateTimeOffset now)
    {
        var today = TunisTime.DateOf(now);
        return Period.Create(
            TunisTime.StartOfDay(today.AddDays(-_options.HorizonDaysBefore)),
            TunisTime.StartOfDay(today.AddDays(_options.HorizonDaysAfter + 1))).Value;
    }

    /// <summary>A value loaded at most once, on the thread pool, whatever the number of concurrent readers.</summary>
    private sealed class CacheEntry<T>
    {
        private readonly Lazy<Task<T>> _value;

        public CacheEntry(Func<Task<T>> load, DateTimeOffset createdAt)
        {
            // Task.Run: the factory returns at once, so readers never block on the Lazy while the load runs.
            _value = new Lazy<Task<T>>(() => Task.Run(load), LazyThreadSafetyMode.ExecutionAndPublication);
            CreatedAt = createdAt;
        }

        public DateTimeOffset CreatedAt { get; }

        public Task<T> Value => _value.Value;

        public bool IsStale(DateTimeOffset now, TimeSpan expiration) =>
            now - CreatedAt >= expiration || (_value.IsValueCreated && _value.Value is { IsFaulted: true } or { IsCanceled: true });
    }
}
