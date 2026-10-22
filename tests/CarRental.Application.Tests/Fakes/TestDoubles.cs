using System.Data;
using CarRental.Application.Common.Auditing;
using CarRental.Application.Common.Outbox;
using CarRental.Application.Common.Paging;
using CarRental.Application.Common.Persistence;
using CarRental.Application.Common.Security;
using CarRental.Application.Identity.Contracts;
using CarRental.Application.Identity.Users;
using CarRental.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace CarRental.Application.Tests.Fakes;

internal static class TestUsers
{
    /// <summary>The agency of the staff members of these tests (ADR-0067).</summary>
    public static readonly Guid AgencyId = Guid.CreateVersion7();

    /// <summary>
    /// The actor of a test: a member of <see cref="AgencyId"/> with the presets of <paramref name="role"/>, or the support
    /// (outside any agency, across agencies) for <see cref="Roles.Support"/>.
    /// </summary>
    public static ScopedUserContext ContextFor(Guid userId, string userName, string role)
    {
        var context = new ScopedUserContext([]);
        var support = role == Roles.Support;
        context.Initialize(UserContextSnapshot.ForUser(userId, userName, RolePresets.PermissionsOf(role), "203.0.113.10", "corr-1") with
        {
            AgencyId = support ? null : AgencyId,
            AllAgencies = support,
        });
        return context;
    }
}

internal sealed class FakeUserDirectory : IUserDirectory
{
    public List<UserSummary> Users { get; } = [];

    public List<NewUser> Created { get; } = [];

    public List<(Guid UserId, bool Locked)> LockChanges { get; } = [];

    public List<(Guid UserId, string Permission, bool Granted)> PermissionChanges { get; } = [];

    /// <summary>Adds a member of <see cref="TestUsers.AgencyId"/> (a support account for <see cref="Roles.Support"/>).</summary>
    public UserSummary Add(string email, string role, bool locked = false, params string[] extraPermissions) =>
        AddTo(role == Roles.Support ? null : TestUsers.AgencyId, email, role, locked, extraPermissions);

    /// <summary>Adds a member of <paramref name="agencyId"/> (a support account when <see langword="null"/>).</summary>
    public UserSummary AddTo(Guid? agencyId, string email, string role, bool locked = false, params string[] extraPermissions)
    {
        var user = new UserSummary(
            Guid.CreateVersion7(),
            email,
            email,
            [role],
            TwoFactorEnabled: true,
            IsLockedOut: locked,
            LockoutEndUtc: locked ? DateTimeOffset.MaxValue : null,
            HasPassword: true,
            CreatedAtUtc: DateTimeOffset.UnixEpoch,
            LastSignInAtUtc: null,
            ExtraPermissions: extraPermissions,
            AgencyId: agencyId);
        Users.Add(user);
        return user;
    }

    public Task<Result> AddExtraPermissionAsync(Guid userId, string permission, CancellationToken cancellationToken) =>
        ChangePermission(userId, permission, granted: true);

    public Task<Result> RemoveExtraPermissionAsync(Guid userId, string permission, CancellationToken cancellationToken) =>
        ChangePermission(userId, permission, granted: false);

    private Task<Result> ChangePermission(Guid userId, string permission, bool granted)
    {
        PermissionChanges.Add((userId, permission, granted));
        var index = Users.FindIndex(u => u.Id == userId);
        var permissions = Users[index].ExtraPermissions.Where(p => p != permission).ToList();
        if (granted)
        {
            permissions.Add(permission);
        }

        Users[index] = Users[index] with { ExtraPermissions = [.. permissions.Order(StringComparer.Ordinal)] };
        return Task.FromResult(Result.Success());
    }

    public Task<PagedResult<UserSummary>> ListAsync(Guid? agencyId, string? search, PageRequest page, CancellationToken cancellationToken)
    {
        var matches = Users.Where(u => u.AgencyId == agencyId && (search is null || u.Email.Contains(search, StringComparison.OrdinalIgnoreCase))).ToList();
        return Task.FromResult(new PagedResult<UserSummary>(matches.Skip(page.Skip).Take(page.PageSize).ToList(), matches.Count, page.Page, page.PageSize));
    }

    public Task<UserSummary?> FindAsync(Guid userId, CancellationToken cancellationToken) =>
        Task.FromResult(Users.FirstOrDefault(u => u.Id == userId));

    public Task<bool> EmailExistsAsync(string email, CancellationToken cancellationToken) =>
        Task.FromResult(Users.Any(u => string.Equals(u.Email, email, StringComparison.OrdinalIgnoreCase)));

    public Task<Result<Guid>> CreateAsync(NewUser user, CancellationToken cancellationToken)
    {
        Created.Add(user);
        var added = AddTo(user.AgencyId, user.Email, user.Role);
        return Task.FromResult(Result.Success(added.Id));
    }

    public Task<Result> SetLockedAsync(Guid userId, bool locked, CancellationToken cancellationToken)
    {
        LockChanges.Add((userId, locked));
        var index = Users.FindIndex(u => u.Id == userId);
        Users[index] = Users[index] with { IsLockedOut = locked };
        return Task.FromResult(Result.Success());
    }

    public Dictionary<Guid, Guid?> SelectedAgencies { get; } = [];

    public Task<Guid?> FindSelectedAgencyAsync(Guid userId, CancellationToken cancellationToken) =>
        Task.FromResult(SelectedAgencies.GetValueOrDefault(userId));

    public Task<Result> SetSelectedAgencyAsync(Guid userId, Guid? agencyId, CancellationToken cancellationToken)
    {
        SelectedAgencies[userId] = agencyId;
        return Task.FromResult(Result.Success());
    }

    public Task<int> CountActiveUsersInRoleAsync(string role, Guid? agencyId, CancellationToken cancellationToken) =>
        Task.FromResult(Users.Count(u => !u.IsLockedOut && u.AgencyId == agencyId && u.Roles.Contains(role)));

    public Task<IReadOnlyDictionary<Guid, string>> GetDisplayNamesAsync(IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<Guid, string>>(Users.Where(u => userIds.Contains(u.Id)).ToDictionary(u => u.Id, u => u.DisplayName));
}

internal sealed class RecordingUserSessions : IUserSessions
{
    public List<Guid> Refreshed { get; } = [];

    public void RefreshClaims(Guid userId) => Refreshed.Add(userId);
}

internal sealed class RecordingInvitationSender : IUserInvitationSender
{
    public List<Guid> Invited { get; } = [];

    public Task SendInvitationAsync(Guid userId, CancellationToken cancellationToken)
    {
        Invited.Add(userId);
        return Task.CompletedTask;
    }
}

internal sealed class RecordingAuditTrail : IAuditTrail
{
    public List<AuditEvent> Events { get; } = [];

    public Task RecordAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
    {
        Events.Add(auditEvent);
        return Task.CompletedTask;
    }
}

/// <summary>Runs the operation inline, without a database.</summary>
internal sealed class InlineUnitOfWork : IUnitOfWork
{
    public int Executions { get; private set; }

    public bool HasActiveTransaction { get; private set; }

    public async Task<Result> ExecuteAsync(
        Func<CancellationToken, Task<Result>> operation,
        IsolationLevel isolationLevel = IsolationLevel.ReadCommitted,
        CancellationToken cancellationToken = default)
    {
        Executions++;
        HasActiveTransaction = true;
        try
        {
            return await operation(cancellationToken);
        }
        finally
        {
            HasActiveTransaction = false;
        }
    }

    public async Task<Result<T>> ExecuteAsync<T>(
        Func<CancellationToken, Task<Result<T>>> operation,
        IsolationLevel isolationLevel = IsolationLevel.ReadCommitted,
        CancellationToken cancellationToken = default)
    {
        Executions++;
        HasActiveTransaction = true;
        try
        {
            return await operation(cancellationToken);
        }
        finally
        {
            HasActiveTransaction = false;
        }
    }
}

internal sealed class RecordingOutbox : IOutbox
{
    public List<object> Messages { get; } = [];

    public void Enqueue<TMessage>(TMessage message)
        where TMessage : class => Messages.Add(message);
}

/// <summary>A context that only counts saves (no entity sets).</summary>
internal sealed class CountingDbContext : IAppDbContext
{
    public int Saves { get; private set; }

    public DbSet<TEntity> Set<TEntity>()
        where TEntity : class => throw new NotSupportedException("No entity set in this test double.");

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        Saves++;
        return Task.FromResult(0);
    }
}
