using System.Collections.Frozen;

namespace CarRental.Application.Common.Security;

/// <summary>Immutable description of the actor of an operation; copied into each DI scope that runs on its behalf.</summary>
public sealed record UserContextSnapshot
{
    public Guid? UserId { get; init; }

    public string? UserName { get; init; }

    public bool IsAuthenticated { get; init; }

    public bool IsSystem { get; init; }

    public string? IpAddress { get; init; }

    public string? CorrelationId { get; init; }

    public IReadOnlySet<string> Permissions { get; init; } = FrozenSet<string>.Empty;

    /// <summary>
    /// The agency the operation works for (ADR-0067): the agency of a staff member, the agency a support user entered,
    /// the agency of the public site being browsed or the agency a background step works for. <see langword="null"/>
    /// when none: the operation then sees no agency data, unless <see cref="AllAgencies"/>.
    /// </summary>
    public Guid? AgencyId { get; init; }

    /// <summary>
    /// The actor may work for every agency: the system actor (background jobs, startup) and the support. Without
    /// <see cref="AgencyId"/> it reads and writes the data of every agency; with it, the data of that agency only.
    /// </summary>
    public bool AllAgencies { get; init; }

    /// <summary>The operation reads and writes the data of every agency (an <see cref="AllAgencies"/> actor outside any agency).</summary>
    public bool IsAcrossAgencies => AllAgencies && AgencyId is null;

    public static UserContextSnapshot Anonymous(string? ipAddress = null, string? correlationId = null) =>
        new() { IpAddress = ipAddress, CorrelationId = correlationId };

    /// <summary>
    /// Trusted platform actor, e.g. <c>system:outbox</c>, <c>system:seed</c>: across agencies, or for the agency
    /// <paramref name="agencyId"/> only when given.
    /// </summary>
    public static UserContextSnapshot System(string name, string? correlationId = null, Guid? agencyId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (agencyId == Guid.Empty)
        {
            throw new ArgumentException("An agency id cannot be empty.", nameof(agencyId));
        }

        return new() { UserName = name, IsSystem = true, CorrelationId = correlationId, AllAgencies = true, AgencyId = agencyId };
    }

    public static UserContextSnapshot ForUser(
        Guid userId,
        string? userName,
        IEnumerable<string> permissions,
        string? ipAddress = null,
        string? correlationId = null)
    {
        ArgumentNullException.ThrowIfNull(permissions);
        return new()
        {
            UserId = userId,
            UserName = userName,
            IsAuthenticated = true,
            IpAddress = ipAddress,
            CorrelationId = correlationId,
            Permissions = permissions.ToFrozenSet(StringComparer.Ordinal),
        };
    }
}
