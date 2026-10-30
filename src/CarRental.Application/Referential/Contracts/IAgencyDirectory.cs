using CarRental.Domain.Common;

namespace CarRental.Application.Referential.Contracts;

/// <summary>
/// The agencies of the platform as the routing (public site address, own host names), the sign-in and the module checks
/// see them (ADR-0067, ADR-0069): read across agencies, whatever the agency of the operation, kept in memory and dropped
/// after any committed change of an agency or of its host names. Not for business screens: they read the agency of the
/// operation through the persistence layer.
/// </summary>
public interface IAgencyDirectory
{
    /// <summary>The agency whose public site address is <paramref name="slug"/> (any case), active or not; <see langword="null"/> when none.</summary>
    Task<AgencySummary?> FindBySlugAsync(string? slug, CancellationToken cancellationToken = default);

    /// <summary>
    /// The agency whose own site is served at <paramref name="host"/> (a host name, any case, without port), active or
    /// not; <see langword="null"/> when no agency holds that host name (ADR-0069).
    /// </summary>
    Task<AgencySummary?> FindByHostAsync(string? host, CancellationToken cancellationToken = default);

    /// <summary>The agency, active or not; <see langword="null"/> when it does not exist.</summary>
    Task<AgencySummary?> FindAsync(Guid agencyId, CancellationToken cancellationToken = default);

    /// <summary>Every agency, active or not, by name.</summary>
    Task<IReadOnlyList<AgencySummary>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>True when the agency exists, is active and has every module of <paramref name="module"/> on.</summary>
    Task<bool> HasModuleAsync(Guid agencyId, AgencyModules module, CancellationToken cancellationToken = default);
}

/// <summary>
/// An agency as <see cref="IAgencyDirectory"/> gives it. <paramref name="PrimaryHost"/> is the host name its own site is
/// served at (its links then carry no address segment: <c>https://www.agence.tn/fr</c>), <see langword="null"/> when the
/// site lives on the platform host only (<c>/{slug}/fr</c>); <paramref name="Hosts"/> every host name it holds (ADR-0069).
/// </summary>
public sealed record AgencySummary(
    Guid Id,
    string Name,
    string Slug,
    string City,
    bool IsActive,
    AgencyModules Modules,
    string? PrimaryHost = null,
    IReadOnlyList<string>? Hosts = null)
{
    /// <summary>Every host name of the agency's own site (the primary one included), none when it has no own site.</summary>
    public IReadOnlyList<string> Hosts { get; init; } = Hosts ?? [];

    /// <summary>True when every module of <paramref name="module"/> is on (whether the agency is active or not).</summary>
    public bool HasModule(AgencyModules module) => (Modules & module) == module;

    /// <summary>The public site answers: the agency is active and its public site module is on.</summary>
    public bool HasOpenPublicSite => IsActive && HasModule(AgencyModules.PublicSite);

    /// <summary>
    /// The first segment of the links written for the customers of the agency outside any request (e-mails, bot): the
    /// address of its site on the platform host, or nothing when its site is served on its own host (ADR-0069).
    /// </summary>
    public string LinkSlug => PrimaryHost is null ? Slug : string.Empty;

    /// <summary>
    /// The base address of the links written for the customers of the agency outside any request: its own site
    /// (<c>https://www.agence.tn/</c>) when it has one, else <paramref name="platformBaseUrl"/> (the platform's address,
    /// <c>PublicSite:BaseUrl</c> or the address the customer used).
    /// </summary>
    public Uri? LinkBaseUrl(Uri? platformBaseUrl) =>
        PrimaryHost is { } host ? new Uri($"https://{host}/", UriKind.Absolute) : platformBaseUrl;

    /// <summary>True when <paramref name="host"/> (any case) is one of the host names of the agency's own site.</summary>
    public bool HoldsHost(string? host) =>
        !string.IsNullOrWhiteSpace(host) && Hosts.Contains(host.Trim().TrimEnd('.').ToLowerInvariant(), StringComparer.Ordinal);
}
