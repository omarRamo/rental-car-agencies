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
/// User administration for the manager (back office <c>/admin/users</c>): list, create with a role (invitation e-mail),
/// lock and unlock. Every change is audited (explicit event plus the automatic entity diff).
/// </summary>
public sealed class UserAdministrationService(
    IUserDirectory directory,
    IUserInvitationSender invitations,
    IAuditTrail auditTrail,
    IUserContext userContext,
    IUnitOfWork unitOfWork)
{
    public const int DisplayNameMaxLength = 100;
    private const string UserEntityType = "User";

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

        return await directory.ListAsync(string.IsNullOrWhiteSpace(search) ? null : search.Trim(), page, cancellationToken);
    }

    public async Task<Result<Guid>> CreateUserAsync(CreateUserRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!userContext.HasPermission(Permissions.UsersManage))
        {
            return IdentityErrors.Forbidden;
        }

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

        if (!Roles.IsKnown(request.Role))
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

                var created = await directory.CreateAsync(new NewUser(email, displayName, request.Role), ct);
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
                    },
                    ct);
                return created;
            },
            cancellationToken: cancellationToken);
    }

    public Task<Result> LockAsync(Guid userId, CancellationToken cancellationToken = default) =>
        SetLockedAsync(userId, locked: true, cancellationToken);

    public Task<Result> UnlockAsync(Guid userId, CancellationToken cancellationToken = default) =>
        SetLockedAsync(userId, locked: false, cancellationToken);

    private static bool IsValidEmail(string email) =>
        email.Length is > 3 and <= 254
        && MailAddress.TryCreate(email, out var address)
        && string.Equals(address.Address, email, StringComparison.OrdinalIgnoreCase);

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
                var target = await directory.FindAsync(userId, ct);
                if (target is null)
                {
                    return IdentityErrors.UserNotFound;
                }

                if (target.IsLockedOut == locked)
                {
                    return Result.Success();
                }

                if (locked
                    && target.Roles.Contains(Roles.Manager, StringComparer.Ordinal)
                    && await directory.CountActiveUsersInRoleAsync(Roles.Manager, ct) <= 1)
                {
                    return IdentityErrors.LastManager;
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
                    },
                    ct);
                return Result.Success();
            },
            cancellationToken: cancellationToken);
    }
}
