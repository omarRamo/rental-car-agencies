using CarRental.Application.Common.Paging;
using CarRental.Domain.Common;

namespace CarRental.Application.Identity.Users;

/// <summary>
/// Port to the user store (ASP.NET Core Identity in Infrastructure). Primitive operations only:
/// rules (permissions, last manager, self-lock) are enforced by <see cref="UserAdministrationService"/>.
/// </summary>
public interface IUserDirectory
{
    Task<PagedResult<UserSummary>> ListAsync(string? search, PageRequest page, CancellationToken cancellationToken);

    Task<UserSummary?> FindAsync(Guid userId, CancellationToken cancellationToken);

    Task<bool> EmailExistsAsync(string email, CancellationToken cancellationToken);

    /// <summary>Creates a user without password (set through the invitation link) with the given role.</summary>
    Task<Result<Guid>> CreateAsync(NewUser user, CancellationToken cancellationToken);

    /// <summary>Locks (indefinitely, sessions revoked) or unlocks an account.</summary>
    Task<Result> SetLockedAsync(Guid userId, bool locked, CancellationToken cancellationToken);

    /// <summary>Users of <paramref name="role"/> that are not locked out.</summary>
    Task<int> CountActiveUsersInRoleAsync(string role, CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<Guid, string>> GetDisplayNamesAsync(IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken);

    /// <summary>Grants an extra permission to the user (a permission claim of the user, on top of the role presets).</summary>
    Task<Result> AddExtraPermissionAsync(Guid userId, string permission, CancellationToken cancellationToken);

    /// <summary>Revokes an extra permission of the user.</summary>
    Task<Result> RemoveExtraPermissionAsync(Guid userId, string permission, CancellationToken cancellationToken);
}

/// <summary>Sends the invitation e-mail of a new user (link to choose a password), through the e-mail outbox.</summary>
public interface IUserInvitationSender
{
    Task SendInvitationAsync(Guid userId, CancellationToken cancellationToken);
}

/// <summary>
/// The open sessions of the users (web host): after a change of permissions, the sessions of the user reload their
/// claims at their next request, without signing in again.
/// </summary>
public interface IUserSessions
{
    void RefreshClaims(Guid userId);
}

public sealed record NewUser(string Email, string DisplayName, string Role);

/// <summary>
/// A user as listed in the back office; <c>ExtraPermissions</c> are the permissions granted to the user on top of the
/// presets of their roles, ordered.
/// </summary>
public sealed record UserSummary(
    Guid Id,
    string Email,
    string DisplayName,
    IReadOnlyList<string> Roles,
    bool TwoFactorEnabled,
    bool IsLockedOut,
    DateTimeOffset? LockoutEndUtc,
    bool HasPassword,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? LastSignInAtUtc,
    IReadOnlyList<string> ExtraPermissions);

/// <summary>Request of the "create user" form.</summary>
public sealed record CreateUserRequest(string Email, string DisplayName, string Role);

/// <summary>The permissions of a user: those of their role presets, their extra ones, and everything they include.</summary>
public sealed record UserPermissions(
    Guid UserId,
    string Email,
    string DisplayName,
    IReadOnlyList<string> Roles,
    IReadOnlySet<string> RolePermissions,
    IReadOnlySet<string> ExtraPermissions,
    IReadOnlySet<string> EffectivePermissions);
