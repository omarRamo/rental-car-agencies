using CarRental.Application.Referential.Contracts;
using CarRental.Domain.Common;

namespace CarRental.Application.Tests.Fakes;

/// <summary>
/// The agency directory of a test (ADR-0067): the agencies it declares, active with the modules it gives them (every
/// module by default); a test switches a module off or suspends an agency with <see cref="Set"/>.
/// </summary>
internal sealed class StaticAgencyDirectory : IAgencyDirectory
{
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, AgencySummary> _agencies = [];

    public StaticAgencyDirectory(params Guid[] agencyIds)
    {
        foreach (var agencyId in agencyIds)
        {
            Set(agencyId, AgencyModuleCatalog.Every);
        }
    }

    /// <summary>Declares (or replaces) an agency with these modules.</summary>
    public AgencySummary Set(Guid agencyId, AgencyModules modules, bool active = true)
    {
        var agency = new AgencySummary(agencyId, $"Agence {agencyId:N}", $"agence-{agencyId:N}", "Tunis", active, modules);
        lock (_gate)
        {
            _agencies[agencyId] = agency;
        }

        return agency;
    }

    public Task<AgencySummary?> FindBySlugAsync(string? slug, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_agencies.Values.FirstOrDefault(a => string.Equals(a.Slug, slug?.Trim(), StringComparison.OrdinalIgnoreCase)));
        }
    }

    public Task<AgencySummary?> FindByHostAsync(string? host, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_agencies.Values.FirstOrDefault(a => a.HoldsHost(host)));
        }
    }

    public Task<AgencySummary?> FindAsync(Guid agencyId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_agencies.GetValueOrDefault(agencyId));
        }
    }

    public Task<IReadOnlyList<AgencySummary>> ListAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<AgencySummary>>([.. _agencies.Values.OrderBy(a => a.Name, StringComparer.Ordinal)]);
        }
    }

    public async Task<bool> HasModuleAsync(Guid agencyId, AgencyModules module, CancellationToken cancellationToken = default) =>
        await FindAsync(agencyId, cancellationToken) is { IsActive: true } agency && agency.HasModule(module);
}
