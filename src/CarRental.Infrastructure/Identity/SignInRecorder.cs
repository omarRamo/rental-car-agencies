using System.Security.Claims;
using CarRental.Application.Common.Auditing;
using CarRental.Domain.Identity;
using CarRental.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CarRental.Infrastructure.Identity;

/// <summary>
/// Bookkeeping of the sign-in pages: last sign-in date and audit events (success, failure, lockout, account changes).
/// Safe under concurrency: several sign-ins of the same user at the same moment (two tabs, a double submission, a
/// retry) never fail, and each one is audited.
/// </summary>
public sealed partial class SignInRecorder(
    CarRentalDbContext db,
    IAuditTrail auditTrail,
    TimeProvider timeProvider,
    ILogger<SignInRecorder> logger)
{
    private const string UserEntityType = "User";

    public async Task SucceededAsync(ApplicationUser user, string method, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        DiscardFailedIdentityChanges();
        var now = timeProvider.GetUtcNow();

        // A targeted update instead of UserManager.UpdateAsync: no concurrency stamp is involved, so it cannot conflict
        // with a parallel sign-in of the same user or with an administrator's change, and the date never moves backwards.
        // The column is left out of the audit log; the sign-in itself is audited below.
        await db.Users
            .Where(u => u.Id == user.Id && (u.LastSignInAtUtc == null || u.LastSignInAtUtc < now))
            .ExecuteUpdateAsync(setters => setters.SetProperty(u => u.LastSignInAtUtc, now), cancellationToken);

        await auditTrail.RecordAsync(
            new AuditEvent(AuditActions.LoginSucceeded)
            {
                EntityType = UserEntityType,
                EntityId = user.Id.ToString(),
                Details = method,
                ActingUserId = user.Id,
                ActingUserName = user.UserName,
            },
            cancellationToken);
    }

    public Task FailedAsync(ApplicationUser? user, string reason, string? attemptedEmail = null, CancellationToken cancellationToken = default)
    {
        DiscardFailedIdentityChanges();
        return auditTrail.RecordAsync(
            new AuditEvent(AuditActions.LoginFailed)
            {
                EntityType = UserEntityType,
                EntityId = user?.Id.ToString(),
                Details = user is null ? $"{reason} ({attemptedEmail})" : reason,
            },
            cancellationToken);
    }

    public Task LockedOutAsync(ApplicationUser user, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        DiscardFailedIdentityChanges();
        return auditTrail.RecordAsync(
            new AuditEvent(AuditActions.LockedOut)
            {
                EntityType = UserEntityType,
                EntityId = user.Id.ToString(),
                ActingUserId = user.Id,
                ActingUserName = user.UserName,
            },
            cancellationToken);
    }

    public Task AccountEventAsync(string action, ClaimsPrincipal principal, string? details = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        ArgumentNullException.ThrowIfNull(principal);
        DiscardFailedIdentityChanges();
        return auditTrail.RecordAsync(
            new AuditEvent(action)
            {
                EntityType = UserEntityType,
                EntityId = Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var userId) ? userId.ToString() : null,
                Details = details,
            },
            cancellationToken);
    }

    /// <summary>
    /// <see cref="Microsoft.AspNetCore.Identity.UserManager{TUser}"/> saves every change at once: a change still pending
    /// here is what a failed save left behind, typically the concurrency failure of a parallel sign-in that updated the
    /// same failed-access counter. The audit event must not save it again — it would fail the same way and turn the
    /// sign-in into an error page — so the stale entries (and the audit rows prepared for them) are dropped.
    /// </summary>
    private void DiscardFailedIdentityChanges()
    {
        if (!db.ChangeTracker.HasChanges())
        {
            return;
        }

        LogDiscarded(db.ChangeTracker.Entries().Count(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted));
        db.ChangeTracker.Clear();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Sign-in bookkeeping discarded {Count} change(s) left by a concurrent Identity update.")]
    private partial void LogDiscarded(int count);
}
