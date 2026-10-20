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
}

/// <summary>Sends the invitation e-mail of a new user (link to choose a password), through the e-mail outbox.</summary>
public interface IUserInvitationSender
{
    Task SendInvitationAsync(Guid userId, CancellationToken cancellationToken);
}

public sealed record NewUser(string Email, string DisplayName, string Role);

/// <summary>A user as listed in the back office.</summary>
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
    DateTimeOffset? LastSignInAtUtc);

/// <summary>Request of the "create user" form.</summary>
public sealed record CreateUserRequest(string Email, string DisplayName, string Role);
