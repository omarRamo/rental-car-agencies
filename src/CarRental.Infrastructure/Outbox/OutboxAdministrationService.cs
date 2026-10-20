using System.Globalization;
using CarRental.Application.Common.Auditing;
using CarRental.Application.Common.Outbox;
using CarRental.Application.Common.Paging;
using CarRental.Application.Common.Persistence;
using CarRental.Application.Common.Security;
using CarRental.Application.Identity.Contracts;
using CarRental.Domain.Common;
using CarRental.Domain.Identity;
using CarRental.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CarRental.Infrastructure.Outbox;

/// <summary>State of an unprocessed outbox message on the administration screen.</summary>
public enum OutboxEntryState
{
    /// <summary>Not tried yet: the next processor run handles it.</summary>
    Pending = 1,

    /// <summary>Failed at least once; retried automatically at <see cref="OutboxEntry.NextAttemptAtUtc"/>.</summary>
    Retrying = 2,

    /// <summary>No automatic attempt left: waits for a manual retry.</summary>
    Failed = 3,
}

/// <summary>Which unprocessed messages the screen lists.</summary>
public enum OutboxListFilter
{
    Failed = 1,
    Pending = 2,
    All = 3,
}

/// <summary>An unprocessed outbox message (its payload is not shown: it may hold personal data).</summary>
public sealed record OutboxEntry(
    Guid Id,
    string Type,
    DateTimeOffset OccurredAtUtc,
    OutboxEntryState State,
    int AttemptCount,
    DateTimeOffset? NextAttemptAtUtc,
    string? LastError,
    string? CorrelationId)
{
    /// <summary>The class name of the message, without its namespace.</summary>
    public string ShortType => Type[(Type.LastIndexOf('.') + 1)..];
}

public sealed record OutboxOverview(OutboxStatistics Statistics, int MaxAttempts, TimeSpan PendingAlertAge);

/// <summary>
/// The outbox screen (<c>/admin/outbox</c>, permission <c>outbox.manage</c>, re-checked here): the state of the queue,
/// the failed and pending messages with their last error, and the retry of a message (audited, then processed at once).
/// The outbox is an infrastructure mechanism, so its administration lives next to the processor (ADR-0028).
/// </summary>
public sealed class OutboxAdministrationService(
    CarRentalDbContext db,
    IUnitOfWork unitOfWork,
    IUserContext userContext,
    IAuditTrail auditTrail,
    IOutboxDispatcher dispatcher,
    IOptions<OutboxOptions> options)
{
    private const string EntityType = "OutboxMessage";

    public async Task<Result<OutboxOverview>> GetOverviewAsync(CancellationToken cancellationToken = default)
    {
        if (!userContext.HasPermission(Permissions.OutboxManage))
        {
            return OutboxErrors.Forbidden;
        }

        var settings = options.Value;
        var statistics = await OutboxStatistics.ReadAsync(db, settings.MaxAttempts, cancellationToken);
        return new OutboxOverview(statistics, settings.MaxAttempts, settings.PendingAlertAge);
    }

    /// <summary>Unprocessed messages of <paramref name="filter"/>, oldest first.</summary>
    public async Task<Result<PagedResult<OutboxEntry>>> ListAsync(OutboxListFilter filter, PageRequest page, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        if (!userContext.HasPermission(Permissions.OutboxManage))
        {
            return OutboxErrors.Forbidden;
        }

        var maxAttempts = options.Value.MaxAttempts;
        var query = db.Set<OutboxMessage>().AsNoTracking().Where(m => m.ProcessedAtUtc == null);
        query = filter switch
        {
            OutboxListFilter.Failed => query.Where(m => m.AttemptCount >= maxAttempts),
            OutboxListFilter.Pending => query.Where(m => m.AttemptCount < maxAttempts),
            _ => query,
        };

        var total = await query.CountAsync(cancellationToken);
        var rows = await query
            .OrderBy(m => m.OccurredAtUtc)
            .ThenBy(m => m.Id)
            .Skip(page.Skip)
            .Take(page.PageSize)
            .Select(m => new { m.Id, m.Type, m.OccurredAtUtc, m.AttemptCount, m.NextAttemptAtUtc, m.LastError, m.CorrelationId })
            .ToListAsync(cancellationToken);

        var items = rows
            .Select(m => new OutboxEntry(
                m.Id,
                m.Type,
                m.OccurredAtUtc,
                m.AttemptCount >= maxAttempts ? OutboxEntryState.Failed : m.AttemptCount > 0 ? OutboxEntryState.Retrying : OutboxEntryState.Pending,
                m.AttemptCount,
                m.AttemptCount >= maxAttempts ? null : m.NextAttemptAtUtc,
                m.LastError,
                m.CorrelationId))
            .ToList();
        return new PagedResult<OutboxEntry>(items, total, page.Page, page.PageSize);
    }

    /// <summary>
    /// Makes an unprocessed message due now with a full set of automatic attempts (its last error stays visible until it
    /// is processed), audits it, and asks for an immediate processing run. Handlers are idempotent (ADR-0013).
    /// </summary>
    public async Task<Result> RetryAsync(Guid messageId, CancellationToken cancellationToken = default)
    {
        if (!userContext.HasPermission(Permissions.OutboxManage))
        {
            return OutboxErrors.Forbidden;
        }

        var result = await unitOfWork.ExecuteAsync(
            async ct =>
            {
                var message = await db.Set<OutboxMessage>()
                    .AsNoTracking()
                    .Where(m => m.Id == messageId && m.ProcessedAtUtc == null)
                    .Select(m => new { m.Type, m.AttemptCount })
                    .FirstOrDefaultAsync(ct);
                if (message is null)
                {
                    return OutboxErrors.MessageNotFound;
                }

                await db.Set<OutboxMessage>()
                    .Where(m => m.Id == messageId && m.ProcessedAtUtc == null)
                    .ExecuteUpdateAsync(
                        setters => setters
                            .SetProperty(m => m.AttemptCount, 0)
                            .SetProperty(m => m.NextAttemptAtUtc, (DateTimeOffset?)null),
                        ct);

                await auditTrail.RecordAsync(
                    new AuditEvent(AuditActions.OutboxMessageRetried)
                    {
                        EntityType = EntityType,
                        EntityId = messageId.ToString(),
                        Details = string.Create(CultureInfo.InvariantCulture, $"{message.Type} after {message.AttemptCount} failed attempt(s)"),
                    },
                    ct);
                return Result.Success();
            },
            cancellationToken: cancellationToken);

        if (result.IsSuccess)
        {
            dispatcher.TriggerProcessing();
        }

        return result;
    }
}
