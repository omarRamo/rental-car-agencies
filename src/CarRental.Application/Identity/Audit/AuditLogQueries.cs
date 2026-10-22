using CarRental.Application.Common.Paging;
using CarRental.Application.Common.Persistence;
using CarRental.Application.Common.Security;
using CarRental.Application.Identity.Contracts;
using CarRental.Domain.Common;
using CarRental.Domain.Identity;
using Microsoft.EntityFrameworkCore;

namespace CarRental.Application.Identity.Audit;

/// <summary>
/// Read side of the audit log for the back office (<c>/admin/audit</c>, permission <c>audit.read</c>): the journal of the
/// agency the operation works for (the persistence layer hides the others, ADR-0067), or, across agencies (the support on
/// the platform pages), the journals of every agency and of the platform itself.
/// </summary>
public sealed class AuditLogQueries(IAppDbContext db, IUserContext userContext)
{
    public async Task<Result<PagedResult<AuditLogEntry>>> SearchAsync(
        AuditLogFilter filter,
        PageRequest page,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(page);
        if (!userContext.HasPermission(Permissions.AuditRead))
        {
            return IdentityErrors.Forbidden;
        }

        var query = Apply(db.Set<AuditLog>().AsNoTracking(), filter);
        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(a => a.Id)
            .Skip(page.Skip)
            .Take(page.PageSize)
            .Select(a => new AuditLogEntry(
                a.Id,
                a.OccurredAtUtc,
                a.Action,
                a.UserId,
                a.UserName,
                a.IpAddress,
                a.CorrelationId,
                a.EntityType,
                a.EntityId,
                a.Before,
                a.After,
                a.Details,
                a.AgencyId))
            .ToListAsync(cancellationToken);

        return new PagedResult<AuditLogEntry>(items, total, page.Page, page.PageSize);
    }

    /// <summary>Distinct action codes present in the log (for the filter list).</summary>
    public async Task<Result<IReadOnlyList<string>>> GetActionsAsync(CancellationToken cancellationToken = default)
    {
        if (!userContext.HasPermission(Permissions.AuditRead))
        {
            return IdentityErrors.Forbidden;
        }

        var actions = await db.Set<AuditLog>()
            .AsNoTracking()
            .Select(a => a.Action)
            .Distinct()
            .OrderBy(a => a)
            .ToListAsync(cancellationToken);
        return actions;
    }

    internal static IQueryable<AuditLog> Apply(IQueryable<AuditLog> query, AuditLogFilter filter)
    {
        if (filter.FromUtc is { } from)
        {
            query = query.Where(a => a.OccurredAtUtc >= from);
        }

        if (filter.ToUtc is { } to)
        {
            query = query.Where(a => a.OccurredAtUtc < to);
        }

        if (Clean(filter.User) is { } user)
        {
            query = query.Where(a => a.UserName != null && a.UserName.Contains(user));
        }

        if (Clean(filter.Action) is { } action)
        {
            if (action.EndsWith('*'))
            {
                var prefix = action[..^1];
                query = query.Where(a => a.Action.StartsWith(prefix));
            }
            else
            {
                query = query.Where(a => a.Action == action);
            }
        }

        if (Clean(filter.EntityType) is { } entityType)
        {
            query = query.Where(a => a.EntityType == entityType);
        }

        if (Clean(filter.EntityId) is { } entityId)
        {
            query = query.Where(a => a.EntityId == entityId);
        }

        if (Clean(filter.CorrelationId) is { } correlationId)
        {
            query = query.Where(a => a.CorrelationId == correlationId);
        }

        if (filter.PlatformOnly)
        {
            query = query.Where(a => a.AgencyId == null);
        }
        else if (filter.AgencyId is { } agencyId)
        {
            query = query.Where(a => a.AgencyId == agencyId);
        }

        return query;
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>Filters of the audit log viewer; all optional and combined with AND. <see cref="Action"/> accepts a trailing <c>*</c>.</summary>
public sealed record AuditLogFilter
{
    public DateTimeOffset? FromUtc { get; init; }

    /// <summary>Exclusive upper bound.</summary>
    public DateTimeOffset? ToUtc { get; init; }

    /// <summary>Part of the user name.</summary>
    public string? User { get; init; }

    public string? Action { get; init; }

    public string? EntityType { get; init; }

    public string? EntityId { get; init; }

    public string? CorrelationId { get; init; }

    /// <summary>The entries of one agency (meaningful across agencies: an agency only reads its own journal).</summary>
    public Guid? AgencyId { get; init; }

    /// <summary>The entries of the platform itself only (no agency); takes precedence over <see cref="AgencyId"/>.</summary>
    public bool PlatformOnly { get; init; }
}

/// <summary>An entry of the audit log; <c>AgencyId</c> is the agency it concerns, <see langword="null"/> for the platform itself.</summary>
public sealed record AuditLogEntry(
    long Id,
    DateTimeOffset OccurredAtUtc,
    string Action,
    Guid? UserId,
    string? UserName,
    string? IpAddress,
    string? CorrelationId,
    string? EntityType,
    string? EntityId,
    string? Before,
    string? After,
    string? Details,
    Guid? AgencyId = null);
