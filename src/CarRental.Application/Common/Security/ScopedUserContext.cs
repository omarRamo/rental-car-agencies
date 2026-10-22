using CarRental.Domain.Common;

namespace CarRental.Application.Common.Security;

/// <summary>
/// The <see cref="IUserContext"/> of a DI scope. It is either initialized explicitly (Blazor circuit operations,
/// background jobs) or resolved lazily from the ambient context (HTTP request). Without both it is anonymous:
/// it never falls back to the system actor.
/// </summary>
public sealed class ScopedUserContext(IEnumerable<IAmbientUserContextProvider> ambientProviders) : IUserContext
{
    private UserContextSnapshot? _snapshot;
    private bool _isExplicit;

    /// <summary>The effective snapshot (explicit, ambient or anonymous).</summary>
    public UserContextSnapshot Snapshot => _snapshot ??= ResolveAmbient();

    public Guid? UserId => Snapshot.UserId;

    public string? UserName => Snapshot.UserName;

    public bool IsAuthenticated => Snapshot.IsAuthenticated;

    public bool IsSystem => Snapshot.IsSystem;

    public string? IpAddress => Snapshot.IpAddress;

    public string? CorrelationId => Snapshot.CorrelationId;

    public Guid? AgencyId => Snapshot.AgencyId;

    public bool AllAgencies => Snapshot.AllAgencies;

    public bool IsAcrossAgencies => Snapshot.IsAcrossAgencies;

    /// <summary>
    /// Sets the actor of this scope (replacing an ambient snapshot resolved earlier). Setting a different actor twice is a bug.
    /// </summary>
    public void Initialize(UserContextSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (_isExplicit && !ReferenceEquals(_snapshot, snapshot))
        {
            throw new InvalidOperationException("The user context of this scope is already set.");
        }

        _snapshot = snapshot;
        _isExplicit = true;
    }

    public bool HasPermission(string permission)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(permission);
        var snapshot = Snapshot;
        return snapshot.IsSystem || (snapshot.IsAuthenticated && snapshot.Permissions.Contains(permission));
    }

    public AuditActor ToAuditActor()
    {
        var snapshot = Snapshot;
        return new AuditActor(snapshot.UserId, snapshot.UserName, snapshot.IpAddress, snapshot.CorrelationId);
    }

    private UserContextSnapshot ResolveAmbient()
    {
        foreach (var provider in ambientProviders)
        {
            if (provider.GetCurrent() is { } snapshot)
            {
                return snapshot;
            }
        }

        return UserContextSnapshot.Anonymous();
    }
}
