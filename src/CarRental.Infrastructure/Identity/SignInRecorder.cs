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
            }.ForAccountOf(user.AgencyId),
            cancellationToken);
    }

    public Task FailedAsync(ApplicationUser? user, string reason, string? attemptedEmail = null, CancellationToken cancellationToken = default)
    {
        DiscardFailedIdentityChanges();
        var failed = new AuditEvent(AuditActions.LoginFailed)
        {
            EntityType = UserEntityType,
            EntityId = user?.Id.ToString(),
            Details = user is null ? $"{reason} ({MaskAttemptedLogin(attemptedEmail)})" : reason,
        };
        return auditTrail.RecordAsync(user is null ? failed : failed.ForAccountOf(user.AgencyId), cancellationToken);
    }

    /// <summary>
    /// What the append-only audit trail keeps of a login typed for an unknown account: the domain of an e-mail address
    /// only (<c>***@example.tn</c>), else a neutral mention. Users sometimes type their password in the e-mail field, and
    /// nothing written in the audit trail can ever be removed (ADR-0018, ADR-0062); a known account is recorded by its id.
    /// </summary>
    public static string MaskAttemptedLogin(string? attempted)
    {
        var value = attempted?.Trim() ?? string.Empty;
        var at = value.IndexOf('@', StringComparison.Ordinal);
        if (value.Length == 0)
        {
            return "no login";
        }

        if (at <= 0 || at != value.LastIndexOf('@') || value.Length > 254 || !IsDomainName(value[(at + 1)..]))
        {
            return "not an e-mail address";
        }

        return "***@" + value[(at + 1)..].ToLowerInvariant();
    }

    /// <summary>Labels of letters, digits and hyphens separated by dots, ending with a top-level domain of letters.</summary>
    private static bool IsDomainName(string domain)
    {
        var labels = domain.Split('.');
        return labels.Length >= 2
               && labels.All(l => l.Length is > 0 and <= 63 && l.All(c => char.IsAsciiLetterOrDigit(c) || c == '-') && l[0] != '-' && l[^1] != '-')
               && labels[^1].Length >= 2
               && labels[^1].All(char.IsAsciiLetter);
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
            }.ForAccountOf(user.AgencyId),
            cancellationToken);
    }

    /// <summary>
    /// A change the signed-in user made to their own account (password, authenticator, recovery codes): recorded for the
    /// agency of the account, or for the platform for the support — never for the agency the support entered (ADR-0067).
    /// </summary>
    public async Task AccountEventAsync(string action, ClaimsPrincipal principal, string? details = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        ArgumentNullException.ThrowIfNull(principal);
        DiscardFailedIdentityChanges();
        var userId = Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : (Guid?)null;
        var accountEvent = new AuditEvent(action)
        {
            EntityType = UserEntityType,
            EntityId = userId?.ToString(),
            Details = details,
        };
        var account = userId is { } known
            ? await db.Users.AsNoTracking().Where(u => u.Id == known).Select(u => new { u.AgencyId }).FirstOrDefaultAsync(cancellationToken)
            : null;
        await auditTrail.RecordAsync(account is null ? accountEvent : accountEvent.ForAccountOf(account.AgencyId), cancellationToken);
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
