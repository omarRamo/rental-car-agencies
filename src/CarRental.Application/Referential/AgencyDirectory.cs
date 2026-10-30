using System.Collections.Frozen;
using CarRental.Application.Common.Caching;
using CarRental.Application.Common.Persistence;
using CarRental.Application.Common.Security;
using CarRental.Application.Referential.Contracts;
using CarRental.Domain.Common;
using CarRental.Domain.Referential;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CarRental.Application.Referential;

/// <summary>
/// In-memory copy of every agency (a few rows): the public site resolves its address on every request and the module
/// checks run on every page (ADR-0067). Loaded across agencies as the actor <see cref="ActorName"/>, dropped after any
/// committed change of an <see cref="Agency"/>, whatever wrote it, and after <see cref="TimeToLive"/> as a safety net.
/// </summary>
internal sealed class AgencyDirectory(IServiceScopeFactory scopeFactory, TimeProvider timeProvider) : IAgencyDirectory, ICommittedChangesListener, IDisposable
{
    internal const string ActorName = "system:agency-directory";

    internal static readonly TimeSpan TimeToLive = TimeSpan.FromMinutes(5);

    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private Snapshot? _snapshot;
    private long _version;

    public async Task<AgencySummary?> FindBySlugAsync(string? slug, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(slug) || slug.Length > AgencySlug.MaxLength)
        {
            return null;
        }

        var snapshot = await GetAsync(cancellationToken);
        return snapshot.BySlug.GetValueOrDefault(slug.Trim().ToLowerInvariant());
    }

    public async Task<AgencySummary?> FindByHostAsync(string? host, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(host) || host.Length > AgencyHostName.MaxLength + 1)
        {
            return null;
        }

        var snapshot = await GetAsync(cancellationToken);
        return snapshot.ByHost.GetValueOrDefault(host.Trim().TrimEnd('.').ToLowerInvariant());
    }

    public async Task<AgencySummary?> FindAsync(Guid agencyId, CancellationToken cancellationToken = default)
    {
        var snapshot = await GetAsync(cancellationToken);
        return snapshot.ById.GetValueOrDefault(agencyId);
    }

    public async Task<IReadOnlyList<AgencySummary>> ListAsync(CancellationToken cancellationToken = default) =>
        (await GetAsync(cancellationToken)).All;

    public async Task<bool> HasModuleAsync(Guid agencyId, AgencyModules module, CancellationToken cancellationToken = default) =>
        await FindAsync(agencyId, cancellationToken) is { IsActive: true } agency && agency.HasModule(module);

    public void OnCommitted(IReadOnlySet<Type> changedEntityTypes)
    {
        ArgumentNullException.ThrowIfNull(changedEntityTypes);
        if (changedEntityTypes.Contains(typeof(Agency)) || changedEntityTypes.Contains(typeof(AgencyDomain)))
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

    private async Task<Snapshot> GetAsync(CancellationToken cancellationToken)
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
            var agencies = await LoadAsync(cancellationToken);
            var snapshot = new Snapshot(
                agencies,
                agencies.ToFrozenDictionary(a => a.Id),
                agencies.ToFrozenDictionary(a => a.Slug, StringComparer.Ordinal),
                agencies.SelectMany(a => a.Hosts.Select(host => (host, a))).ToFrozenDictionary(h => h.host, h => h.a, StringComparer.Ordinal),
                timeProvider.GetUtcNow() + TimeToLive);
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

    private async Task<IReadOnlyList<AgencySummary>> LoadAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        scope.ServiceProvider.GetService<ScopedUserContext>()?.Initialize(UserContextSnapshot.System(ActorName));
        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        var agencies = await db.Set<Agency>()
            .AsNoTracking()
            .OrderBy(a => a.Name)
            .Select(a => new AgencySummary(a.Id, a.Name, a.Slug, a.City, a.IsActive, a.Modules))
            .ToListAsync(cancellationToken);

        // The host names of the agencies' own sites (ADR-0069): a handful of rows, read with the agencies.
        var domains = await db.Set<AgencyDomain>()
            .AsNoTracking()
            .OrderByDescending(d => d.IsPrimary)
            .ThenBy(d => d.HostName)
            .Select(d => new { d.AgencyId, d.HostName, d.IsPrimary })
            .ToListAsync(cancellationToken);
        var byAgency = domains.ToLookup(d => d.AgencyId);
        return
        [
            .. agencies.Select(a =>
            {
                var hosts = byAgency[a.Id].ToList();
                return hosts.Count == 0
                    ? a
                    : a with { PrimaryHost = hosts.FirstOrDefault(h => h.IsPrimary)?.HostName ?? hosts[0].HostName, Hosts = [.. hosts.Select(h => h.HostName)] };
            }),
        ];
    }

    private sealed record Snapshot(
        IReadOnlyList<AgencySummary> All,
        FrozenDictionary<Guid, AgencySummary> ById,
        FrozenDictionary<string, AgencySummary> BySlug,
        FrozenDictionary<string, AgencySummary> ByHost,
        DateTimeOffset ExpiresAt)
    {
        public bool IsExpired(DateTimeOffset now) => now >= ExpiresAt;
    }
}
