using System.Globalization;
using System.Net.Mail;
using CarRental.Application.Common.Auditing;
using CarRental.Application.Common.Paging;
using CarRental.Application.Common.Persistence;
using CarRental.Application.Common.Security;
using CarRental.Application.Identity.Contracts;
using CarRental.Domain.Common;
using CarRental.Domain.Identity;

namespace CarRental.Application.Identity.Users;

/// <summary>
/// User administration (back office <c>/admin/users</c>): list, create with a role (invitation e-mail), lock and unlock,
/// extra permissions on top of the role presets. A manager administers the users of their agency only (ADR-0067): the
/// users of another agency and the support accounts do not exist for them. The support administers the users of the
/// agency they entered, creates the first manager of an agency and the support accounts. Every change is audited
/// (explicit event plus the automatic entity diff).
/// </summary>
public sealed class UserAdministrationService(
    IUserDirectory directory,
    IUserInvitationSender invitations,
    IAuditTrail auditTrail,
    IUserContext userContext,
    IUnitOfWork unitOfWork,
    IUserSessions sessions)
{
    public const int DisplayNameMaxLength = 100;
    private const string UserEntityType = "User";

    /// <summary>The users of the agency the operation works for.</summary>
    public async Task<Result<PagedResult<UserSummary>>> ListAsync(
        string? search,
        PageRequest page,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        if (!userContext.HasPermission(Permissions.UsersManage))
        {
            return IdentityErrors.Forbidden;
        }

        if (userContext.AgencyId is not { } agencyId)
        {
            return IdentityErrors.AgencyRequired;
        }

        return await directory.ListAsync(agencyId, string.IsNullOrWhiteSpace(search) ? null : search.Trim(), page, cancellationToken);
    }

    /// <summary>The support accounts (support only).</summary>
    public async Task<Result<PagedResult<UserSummary>>> ListSupportAccountsAsync(PageRequest page, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        if (!userContext.HasPermission(Permissions.PlatformAgencies))
        {
            return IdentityErrors.Forbidden;
        }

        return await directory.ListAsync(agencyId: null, search: null, page, cancellationToken);
    }

    /// <summary>Creates a member of the agency the operation works for, with an agency role, and sends the invitation.</summary>
    public Task<Result<Guid>> CreateUserAsync(CreateUserRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!userContext.HasPermission(Permissions.UsersManage))
        {
            return Task.FromResult(Result.Failure<Guid>(IdentityErrors.Forbidden));
        }

        return userContext.AgencyId is { } agencyId
            ? CreateAsync(request, agencyId, cancellationToken)
            : Task.FromResult(Result.Failure<Guid>(IdentityErrors.AgencyRequired));
    }

    /// <summary>
    /// Creates a member of <paramref name="agencyId"/> from the platform pages (support only), e.g. the first manager of
    /// a new agency, and sends the invitation.
    /// </summary>
    public Task<Result<Guid>> CreateAgencyUserAsync(Guid agencyId, CreateUserRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!userContext.HasPermission(Permissions.PlatformAgencies) || !userContext.IsAcrossAgencies)
        {
            return Task.FromResult(Result.Failure<Guid>(IdentityErrors.Forbidden));
        }

        return agencyId == Guid.Empty
            ? Task.FromResult(Result.Failure<Guid>(IdentityErrors.AgencyRequired))
            : CreateAsync(request, agencyId, cancellationToken);
    }

    /// <summary>Creates a support account (support only) and sends the invitation.</summary>
    public Task<Result<Guid>> CreateSupportAccountAsync(string email, string displayName, CancellationToken cancellationToken = default)
    {
        if (!userContext.HasPermission(Permissions.PlatformAgencies) || !userContext.IsAcrossAgencies)
        {
            return Task.FromResult(Result.Failure<Guid>(IdentityErrors.Forbidden));
        }

        return CreateAsync(new CreateUserRequest(email, displayName, Roles.Support), agencyId: null, cancellationToken);
    }

    public Task<Result> LockAsync(Guid userId, CancellationToken cancellationToken = default) =>
        SetLockedAsync(userId, locked: true, cancellationToken);

    public Task<Result> UnlockAsync(Guid userId, CancellationToken cancellationToken = default) =>
        SetLockedAsync(userId, locked: false, cancellationToken);

    /// <summary>The permissions of a user: from the presets of their roles, granted individually, and in effect.</summary>
    public async Task<Result<UserPermissions>> GetPermissionsAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        if (!userContext.HasPermission(Permissions.UsersManage))
        {
            return IdentityErrors.Forbidden;
        }

        var user = await FindManageableAsync(userId, cancellationToken);
        if (user is null)
        {
            return IdentityErrors.UserNotFound;
        }

        var rolePermissions = RolePermissionsOf(user.Roles);
        var extraPermissions = user.ExtraPermissions.ToHashSet(StringComparer.Ordinal);
        return new UserPermissions(
            user.Id,
            user.Email,
            user.DisplayName,
            user.Roles,
            rolePermissions,
            extraPermissions,
            Permissions.WithImplied(rolePermissions.Concat(extraPermissions)));
    }

    /// <summary>
    /// Makes <paramref name="permissions"/> the extra permissions of the user (SPEC §3: « la permission Tarification peut
    /// être confiée à un agent »): each grant and revocation is audited, permissions already given by the role presets are
    /// not stored, and the sessions of the user reload their claims at their next request. A user cannot change their own
    /// permissions, and a member of an agency never receives a platform permission.
    /// </summary>
    public async Task<Result> SetExtraPermissionsAsync(Guid userId, IReadOnlyCollection<string> permissions, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(permissions);
        if (!userContext.HasPermission(Permissions.UsersManage))
        {
            return IdentityErrors.Forbidden;
        }

        if (userContext.UserId == userId)
        {
            return IdentityErrors.CannotChangeOwnPermissions;
        }

        if (!permissions.All(Permissions.IsKnown))
        {
            return IdentityErrors.PermissionUnknown;
        }

        var changed = false;
        var result = await unitOfWork.ExecuteAsync(
            async ct =>
            {
                changed = false;
                var user = await FindManageableAsync(userId, ct);
                if (user is null)
                {
                    return IdentityErrors.UserNotFound;
                }

                if (user.AgencyId is not null && !permissions.All(Permissions.IsAgencyPermission))
                {
                    return IdentityErrors.PermissionNotForAgency;
                }

                var rolePermissions = RolePermissionsOf(user.Roles);
                var requested = permissions.Where(p => !rolePermissions.Contains(p)).ToHashSet(StringComparer.Ordinal);
                var current = user.ExtraPermissions.ToHashSet(StringComparer.Ordinal);

                foreach (var permission in requested.Except(current).Order(StringComparer.Ordinal))
                {
                    var granted = await directory.AddExtraPermissionAsync(userId, permission, ct);
                    if (granted.IsFailure)
                    {
                        return granted;
                    }

                    await AuditPermissionAsync(AuditActions.PermissionGranted, user, permission, ct);
                    changed = true;
                }

                foreach (var permission in current.Except(requested).Order(StringComparer.Ordinal))
                {
                    var revoked = await directory.RemoveExtraPermissionAsync(userId, permission, ct);
                    if (revoked.IsFailure)
                    {
                        return revoked;
                    }

                    await AuditPermissionAsync(AuditActions.PermissionRevoked, user, permission, ct);
                    changed = true;
                }

                return Result.Success();
            },
            cancellationToken: cancellationToken);

        if (result.IsSuccess && changed)
        {
            sessions.RefreshClaims(userId);
        }

        return result;
    }

    private static bool IsValidEmail(string email) =>
        email.Length is > 3 and <= 254
        && MailAddress.TryCreate(email, out var address)
        && string.Equals(address.Address, email, StringComparison.OrdinalIgnoreCase);

    private static HashSet<string> RolePermissionsOf(IEnumerable<string> roles) =>
        roles.Where(Roles.IsKnown).SelectMany(RolePresets.PermissionsOf).ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// Creates a member of <paramref name="agencyId"/> with an agency role, or a support account when
    /// <see langword="null"/> (role <see cref="Roles.Support"/> only).
    /// </summary>
    private async Task<Result<Guid>> CreateAsync(CreateUserRequest request, Guid? agencyId, CancellationToken cancellationToken)
    {
        var email = request.Email?.Trim() ?? string.Empty;
        if (!IsValidEmail(email))
        {
            return IdentityErrors.EmailInvalid;
        }

        var displayName = request.DisplayName?.Trim() ?? string.Empty;
        if (displayName.Length is 0 or > DisplayNameMaxLength)
        {
            return IdentityErrors.DisplayNameInvalid;
        }

        var roleFits = agencyId is null ? request.Role == Roles.Support : Roles.IsAgencyRole(request.Role);
        if (!roleFits)
        {
            return IdentityErrors.RoleUnknown;
        }

        return await unitOfWork.ExecuteAsync(
            async ct =>
            {
                if (await directory.EmailExistsAsync(email, ct))
                {
                    return Result.Failure<Guid>(IdentityErrors.EmailTaken);
                }

                var created = await directory.CreateAsync(new NewUser(email, displayName, request.Role, agencyId), ct);
                if (created.IsFailure)
                {
                    return created;
                }

                await invitations.SendInvitationAsync(created.Value, ct);
                await auditTrail.RecordAsync(
                    new AuditEvent(AuditActions.UserCreated)
                    {
                        EntityType = UserEntityType,
                        EntityId = created.Value.ToString(),
                        Details = string.Create(CultureInfo.InvariantCulture, $"{email} ({request.Role})"),
                        AgencyId = agencyId,
                    },
                    ct);
                return created;
            },
            cancellationToken: cancellationToken);
    }

    /// <summary>
    /// The user when the operation may administer them: a member of the agency it works for (any agency across agencies),
    /// or a support account for the support. Otherwise <see langword="null"/>, as if the user did not exist.
    /// </summary>
    private async Task<UserSummary?> FindManageableAsync(Guid userId, CancellationToken cancellationToken)
    {
        var user = await directory.FindAsync(userId, cancellationToken);
        if (user is null)
        {
            return null;
        }

        var manageable = user.AgencyId is { } agencyId
            ? userContext.IsAcrossAgencies || userContext.AgencyId == agencyId
            : userContext.HasPermission(Permissions.PlatformAgencies);
        return manageable ? user : null;
    }

    private Task AuditPermissionAsync(string action, UserSummary user, string permission, CancellationToken cancellationToken) =>
        auditTrail.RecordAsync(
            new AuditEvent(action)
            {
                EntityType = UserEntityType,
                EntityId = user.Id.ToString(),
                Details = string.Create(CultureInfo.InvariantCulture, $"{permission} ({user.Email})"),
                AgencyId = user.AgencyId,
            },
            cancellationToken);

    private async Task<Result> SetLockedAsync(Guid userId, bool locked, CancellationToken cancellationToken)
    {
        if (!userContext.HasPermission(Permissions.UsersManage))
        {
            return IdentityErrors.Forbidden;
        }

        if (locked && userContext.UserId == userId)
        {
            return IdentityErrors.CannotLockSelf;
        }

        return await unitOfWork.ExecuteAsync(
            async ct =>
            {
                var target = await FindManageableAsync(userId, ct);
                if (target is null)
                {
                    return IdentityErrors.UserNotFound;
                }

                if (target.IsLockedOut == locked)
                {
                    return Result.Success();
                }

                var lastRole = target.AgencyId is null ? Roles.Support : Roles.Manager;
                if (locked
                    && target.Roles.Contains(lastRole, StringComparer.Ordinal)
                    && await directory.CountActiveUsersInRoleAsync(lastRole, target.AgencyId, ct) <= 1)
                {
                    return target.AgencyId is null ? IdentityErrors.LastSupport : IdentityErrors.LastManager;
                }

                var changed = await directory.SetLockedAsync(userId, locked, ct);
                if (changed.IsFailure)
                {
                    return changed;
                }

                await auditTrail.RecordAsync(
                    new AuditEvent(locked ? AuditActions.UserLocked : AuditActions.UserUnlocked)
                    {
                        EntityType = UserEntityType,
                        EntityId = userId.ToString(),
                        Details = target.Email,
                        AgencyId = target.AgencyId,
                    },
                    ct);
                return Result.Success();
            },
            cancellationToken: cancellationToken);
    }
}

/// <summary><see cref="IAgencyManagerInvitations"/> on <see cref="UserAdministrationService.CreateAgencyUserAsync"/>.</summary>
internal sealed class AgencyManagerInvitations(UserAdministrationService users) : IAgencyManagerInvitations
{
    public Task<Result<Guid>> InviteManagerAsync(Guid agencyId, string email, string displayName, CancellationToken cancellationToken = default) =>
        users.CreateAgencyUserAsync(agencyId, new CreateUserRequest(email, displayName, Roles.Manager), cancellationToken);
}
