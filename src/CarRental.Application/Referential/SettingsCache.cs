using System.Collections.Frozen;
using CarRental.Application.Common.Caching;
using CarRental.Application.Common.Persistence;
using CarRental.Domain.Referential;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CarRental.Application.Referential;

/// <summary>
/// In-memory snapshot of every stored setting value (a few dozen rows), read once instead of one query per setting and
/// per call (ADR-0044). Dropped after any committed change of a <see cref="Setting"/> row, whatever wrote it, and after
/// <see cref="TimeToLive"/> as a safety net.
/// </summary>
internal sealed class SettingsCache(IServiceScopeFactory scopeFactory, TimeProvider timeProvider) : ICommittedChangesListener, IDisposable
{
    internal static readonly TimeSpan TimeToLive = TimeSpan.FromMinutes(5);

    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private Snapshot? _snapshot;
    private long _version;

    /// <summary>Stored values by (key, agency); a platform value has no agency.</summary>
    public async Task<FrozenDictionary<(string Key, Guid? AgencyId), string>> GetAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _snapshot) is { } current && !current.IsExpired(timeProvider.GetUtcNow()))
        {
            return current.Values;
        }

        await _loadGate.WaitAsync(cancellationToken);
        try
        {
            if (Volatile.Read(ref _snapshot) is { } loadedMeanwhile && !loadedMeanwhile.IsExpired(timeProvider.GetUtcNow()))
            {
                return loadedMeanwhile.Values;
            }

            var version = Interlocked.Read(ref _version);
            var values = await LoadAsync(cancellationToken);
            var snapshot = new Snapshot(values, timeProvider.GetUtcNow() + TimeToLive);
            if (Interlocked.Read(ref _version) == version)
            {
                // Not invalidated during the load: later readers may use it.
                Volatile.Write(ref _snapshot, snapshot);
            }

            return snapshot.Values;
        }
        finally
        {
            _loadGate.Release();
        }
    }

    public void OnCommitted(IReadOnlySet<Type> changedEntityTypes)
    {
        ArgumentNullException.ThrowIfNull(changedEntityTypes);
        if (changedEntityTypes.Contains(typeof(Setting)))
        {
            Invalidate();
        }
    }

    public void Invalidate()
    {
        Interlocked.Increment(ref _version);
        Volatile.Write(ref _snapshot, null);
    }

    public void Dispose() => _loadGate.Dispose();

    private async Task<FrozenDictionary<(string Key, Guid? AgencyId), string>> LoadAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var rows = await scope.ServiceProvider.GetRequiredService<IAppDbContext>().Set<Setting>()
            .AsNoTracking()
            .Select(s => new { s.Key, s.AgencyId, s.Value })
            .ToListAsync(cancellationToken);
        return rows.ToFrozenDictionary(r => (r.Key, r.AgencyId), r => r.Value);
    }

    private sealed record Snapshot(FrozenDictionary<(string Key, Guid? AgencyId), string> Values, DateTimeOffset ExpiresAt)
    {
        public bool IsExpired(DateTimeOffset now) => now >= ExpiresAt;
    }
}
