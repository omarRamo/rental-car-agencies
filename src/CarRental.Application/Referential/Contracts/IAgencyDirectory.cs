using CarRental.Domain.Common;

namespace CarRental.Application.Referential.Contracts;

/// <summary>
/// The agencies of the platform as the routing (public site address), the sign-in and the module checks see them
/// (ADR-0067): read across agencies, whatever the agency of the operation, kept in memory and dropped after any committed
/// change of an agency. Not for business screens: they read the agency of the operation through the persistence layer.
/// </summary>
public interface IAgencyDirectory
{
    /// <summary>The agency whose public site address is <paramref name="slug"/> (any case), active or not; <see langword="null"/> when none.</summary>
    Task<AgencySummary?> FindBySlugAsync(string? slug, CancellationToken cancellationToken = default);

    /// <summary>The agency, active or not; <see langword="null"/> when it does not exist.</summary>
    Task<AgencySummary?> FindAsync(Guid agencyId, CancellationToken cancellationToken = default);

    /// <summary>Every agency, active or not, by name.</summary>
    Task<IReadOnlyList<AgencySummary>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>True when the agency exists, is active and has every module of <paramref name="module"/> on.</summary>
    Task<bool> HasModuleAsync(Guid agencyId, AgencyModules module, CancellationToken cancellationToken = default);
}

/// <summary>An agency as <see cref="IAgencyDirectory"/> gives it.</summary>
public sealed record AgencySummary(Guid Id, string Name, string Slug, string City, bool IsActive, AgencyModules Modules)
{
    /// <summary>True when every module of <paramref name="module"/> is on (whether the agency is active or not).</summary>
    public bool HasModule(AgencyModules module) => (Modules & module) == module;

    /// <summary>The public site answers: the agency is active and its public site module is on.</summary>
    public bool HasOpenPublicSite => IsActive && HasModule(AgencyModules.PublicSite);
}
