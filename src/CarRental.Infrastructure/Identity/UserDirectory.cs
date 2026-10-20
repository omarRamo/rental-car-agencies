using System.Security.Claims;
using CarRental.Application.Common.Paging;
using CarRental.Application.Identity;
using CarRental.Application.Identity.Contracts;
using CarRental.Application.Identity.Users;
using CarRental.Domain.Common;
using CarRental.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace CarRental.Infrastructure.Identity;

/// <summary><see cref="IUserDirectory"/> on ASP.NET Core Identity (writes) and EF Core queries (lists).</summary>
internal sealed class UserDirectory(
    UserManager<ApplicationUser> userManager,
    CarRentalDbContext db,
    TimeProvider timeProvider) : IUserDirectory
{
    public async Task<PagedResult<UserSummary>> ListAsync(string? search, PageRequest page, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(page);
        var query = db.Users.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search))
        {
            query = query.Where(u => u.Email!.Contains(search) || u.DisplayName.Contains(search));
        }

        var total = await query.CountAsync(cancellationToken);
        var users = await query
            .OrderBy(u => u.DisplayName)
            .ThenBy(u => u.Email)
            .Skip(page.Skip)
            .Take(page.PageSize)
            .ToListAsync(cancellationToken);

        var userIds = users.Select(u => u.Id).ToList();
        var roles = await RolesOfAsync(userIds, cancellationToken);
        var extraPermissions = await ExtraPermissionsOfAsync(userIds, cancellationToken);
        var now = timeProvider.GetUtcNow();
        var items = users
            .Select(u => ToSummary(u, roles.GetValueOrDefault(u.Id) ?? [], extraPermissions.GetValueOrDefault(u.Id) ?? [], now))
            .ToList();
        return new PagedResult<UserSummary>(items, total, page.Page, page.PageSize);
    }

    public async Task<UserSummary?> FindAsync(Guid userId, CancellationToken cancellationToken)
    {
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user is null)
        {
            return null;
        }

        var roles = await RolesOfAsync([user.Id], cancellationToken);
        var extraPermissions = await ExtraPermissionsOfAsync([user.Id], cancellationToken);
        return ToSummary(user, roles.GetValueOrDefault(user.Id) ?? [], extraPermissions.GetValueOrDefault(user.Id) ?? [], timeProvider.GetUtcNow());
    }

    public async Task<Result> AddExtraPermissionAsync(Guid userId, string permission, CancellationToken cancellationToken)
    {
        var user = await userManager.FindByIdAsync(userId.ToString());
        if (user is null)
        {
            return IdentityErrors.UserNotFound;
        }

        var outcome = await userManager.AddClaimAsync(user, new Claim(Permissions.ClaimType, permission));
        return outcome.Succeeded ? Result.Success() : IdentityErrors.OperationFailed(Describe(outcome));
    }

    public async Task<Result> RemoveExtraPermissionAsync(Guid userId, string permission, CancellationToken cancellationToken)
    {
        var user = await userManager.FindByIdAsync(userId.ToString());
        if (user is null)
        {
            return IdentityErrors.UserNotFound;
        }

        var outcome = await userManager.RemoveClaimAsync(user, new Claim(Permissions.ClaimType, permission));
        return outcome.Succeeded ? Result.Success() : IdentityErrors.OperationFailed(Describe(outcome));
    }

    public Task<bool> EmailExistsAsync(string email, CancellationToken cancellationToken)
    {
        var normalized = userManager.NormalizeEmail(email);
        return db.Users.AnyAsync(u => u.NormalizedEmail == normalized, cancellationToken);
    }

    public async Task<Result<Guid>> CreateAsync(NewUser user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        var entity = new ApplicationUser
        {
            UserName = user.Email,
            Email = user.Email,
            EmailConfirmed = true,
            DisplayName = user.DisplayName,
            LockoutEnabled = true,
            CreatedAtUtc = timeProvider.GetUtcNow(),
        };

        var created = await userManager.CreateAsync(entity);
        if (!created.Succeeded)
        {
            return IdentityErrors.OperationFailed(Describe(created));
        }

        var role = await userManager.AddToRoleAsync(entity, user.Role);
        return role.Succeeded ? entity.Id : IdentityErrors.OperationFailed(Describe(role));
    }

    public async Task<Result> SetLockedAsync(Guid userId, bool locked, CancellationToken cancellationToken)
    {
        var user = await userManager.FindByIdAsync(userId.ToString());
        if (user is null)
        {
            return IdentityErrors.UserNotFound;
        }

        IdentityResult outcome;
        if (locked)
        {
            await userManager.SetLockoutEnabledAsync(user, true);
            outcome = await userManager.SetLockoutEndDateAsync(user, DateTimeOffset.MaxValue);
            if (outcome.Succeeded)
            {
                // Revokes existing sessions at the next security stamp validation.
                outcome = await userManager.UpdateSecurityStampAsync(user);
            }
        }
        else
        {
            outcome = await userManager.SetLockoutEndDateAsync(user, null);
            if (outcome.Succeeded)
            {
                outcome = await userManager.ResetAccessFailedCountAsync(user);
            }
        }

        return outcome.Succeeded ? Result.Success() : IdentityErrors.OperationFailed(Describe(outcome));
    }

    public async Task<int> CountActiveUsersInRoleAsync(string role, CancellationToken cancellationToken)
    {
        var normalizedRole = userManager.NormalizeName(role);
        var now = timeProvider.GetUtcNow();
        return await (
                from user in db.Users
                join userRole in db.UserRoles on user.Id equals userRole.UserId
                join r in db.Roles on userRole.RoleId equals r.Id
                where r.NormalizedName == normalizedRole && (user.LockoutEnd == null || user.LockoutEnd <= now)
                select user.Id)
            .Distinct()
            .CountAsync(cancellationToken);
    }

    public async Task<IReadOnlyDictionary<Guid, string>> GetDisplayNamesAsync(IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken) =>
        await db.Users
            .AsNoTracking()
            .Where(u => userIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.DisplayName, cancellationToken);

    private static UserSummary ToSummary(ApplicationUser user, IReadOnlyList<string> roles, IReadOnlyList<string> extraPermissions, DateTimeOffset now) => new(
        user.Id,
        user.Email ?? string.Empty,
        user.DisplayName,
        roles,
        user.TwoFactorEnabled,
        IsLockedOut: user.LockoutEnd is { } end && end > now,
        LockoutEndUtc: user.LockoutEnd,
        HasPassword: user.PasswordHash is not null,
        user.CreatedAtUtc,
        user.LastSignInAtUtc,
        extraPermissions);

    private static string Describe(IdentityResult result) => string.Join(" ", result.Errors.Select(e => e.Description));

    private async Task<Dictionary<Guid, IReadOnlyList<string>>> RolesOfAsync(List<Guid> userIds, CancellationToken cancellationToken)
    {
        var pairs = await (
                from userRole in db.UserRoles
                join role in db.Roles on userRole.RoleId equals role.Id
                where userIds.Contains(userRole.UserId)
                select new { userRole.UserId, role.Name })
            .ToListAsync(cancellationToken);

        return pairs
            .GroupBy(p => p.UserId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(p => p.Name!).OrderBy(n => n, StringComparer.Ordinal).ToList());
    }

    /// <summary>Permission claims of the users themselves (not of their roles): their extra permissions.</summary>
    private async Task<Dictionary<Guid, IReadOnlyList<string>>> ExtraPermissionsOfAsync(List<Guid> userIds, CancellationToken cancellationToken)
    {
        var pairs = await db.UserClaims
            .AsNoTracking()
            .Where(c => userIds.Contains(c.UserId) && c.ClaimType == Permissions.ClaimType && c.ClaimValue != null)
            .Select(c => new { c.UserId, c.ClaimValue })
            .ToListAsync(cancellationToken);

        return pairs
            .GroupBy(p => p.UserId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<string>)g.Select(p => p.ClaimValue!).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList());
    }
}
