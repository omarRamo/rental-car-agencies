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

    public static UserContextSnapshot Anonymous(string? ipAddress = null, string? correlationId = null) =>
        new() { IpAddress = ipAddress, CorrelationId = correlationId };

    /// <summary>Trusted platform actor, e.g. <c>system:outbox</c>, <c>system:seed</c>.</summary>
    public static UserContextSnapshot System(string name, string? correlationId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return new() { UserName = name, IsSystem = true, CorrelationId = correlationId };
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
