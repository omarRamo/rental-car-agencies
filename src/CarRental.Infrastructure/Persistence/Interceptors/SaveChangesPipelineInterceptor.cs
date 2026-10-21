using CarRental.Application.Common.Security;
using CarRental.Domain.Common;
using CarRental.Domain.Identity;
using CarRental.Infrastructure.Outbox;
using CarRental.Infrastructure.Persistence.Auditing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace CarRental.Infrastructure.Persistence.Interceptors;

/// <summary>
/// The persistence pipeline run before every save, in this order:
/// <list type="number">
/// <item>append-only guard: audit tables can only receive inserts;</item>
/// <item>soft delete: removing an <see cref="ISoftDeletable"/> entity marks it deleted instead;</item>
/// <item>domain events of tracked aggregates are copied to <see cref="OutboxMessage"/> rows;</item>
/// <item>one <see cref="AuditLog"/> row per created, updated, soft-deleted or deleted entity (before/after JSON).</item>
/// </list>
/// Everything is written by the same SaveChanges, hence in the same transaction as the change itself.
/// After a successful commit, the outbox processor is triggered.
/// </summary>
internal sealed class SaveChangesPipelineInterceptor(
    IUserContext userContext,
    TimeProvider timeProvider,
    OutboxSerializer serializer,
    OutboxDispatchSignal signal,
    IOutboxDispatcher dispatcher,
    CommittedChanges committedChanges) : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        Prepare(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        Prepare(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        DispatchIfCommitted(eventData.Context);
        return base.SavedChanges(eventData, result);
    }

    public override ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData,
        int result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        DispatchIfCommitted(eventData.Context);
        return base.SavedChangesAsync(eventData, result, cancellationToken);
    }

    public override void SaveChangesFailed(DbContextErrorEventData eventData)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        ResetIfNoTransaction(eventData.Context);
        base.SaveChangesFailed(eventData);
    }

    public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        ResetIfNoTransaction(eventData.Context);
        return base.SaveChangesFailedAsync(eventData, cancellationToken);
    }

    private static void EnforceAppendOnly(DbContext context)
    {
        foreach (var entry in context.ChangeTracker.Entries())
        {
            if (entry.Entity is AuditLog or DocumentAccessLog && entry.State is EntityState.Modified or EntityState.Deleted)
            {
                throw new InvalidOperationException(
                    $"{entry.Metadata.ClrType.Name} is append-only: existing rows can be neither updated nor deleted.");
            }
        }
    }

    private static void ApplySoftDelete(DbContext context, DateTimeOffset now)
    {
        foreach (var entry in context.ChangeTracker.Entries<ISoftDeletable>().Where(e => e.State == EntityState.Deleted).ToList())
        {
            entry.State = EntityState.Modified;
            entry.Property(nameof(ISoftDeletable.IsDeleted)).CurrentValue = true;
            entry.Property(nameof(ISoftDeletable.DeletedAtUtc)).CurrentValue = now;
        }
    }

    private void Prepare(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        context.ChangeTracker.DetectChanges();
        committedChanges.Record(context.ChangeTracker.Entries()
            .Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .Select(e => e.Metadata.ClrType));
        var now = timeProvider.GetUtcNow();

        EnforceAppendOnly(context);
        ApplySoftDelete(context, now);
        WriteDomainEventsToOutbox(context, now);
        WriteAuditEntries(context, now);

        if (context.ChangeTracker.Entries<OutboxMessage>().Any(e => e.State == EntityState.Added))
        {
            signal.MarkPending();
        }
    }

    private void WriteDomainEventsToOutbox(DbContext context, DateTimeOffset now)
    {
        var aggregates = context.ChangeTracker.Entries<AggregateRoot>()
            .Where(e => e.Entity.DomainEvents.Count > 0)
            .Select(e => e.Entity)
            .ToList();

        foreach (var aggregate in aggregates)
        {
            foreach (var domainEvent in aggregate.DequeueDomainEvents())
            {
                context.Set<OutboxMessage>().Add(OutboxMessage.Create(
                    domainEvent.GetType().FullName!,
                    serializer.Serialize(domainEvent),
                    now,
                    userContext.CorrelationId));
            }
        }
    }

    private void WriteAuditEntries(DbContext context, DateTimeOffset now)
    {
        var entries = context.ChangeTracker.Entries()
            .Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted && !e.Metadata.IsExcludedFromAudit())
            .ToList();
        if (entries.Count == 0)
        {
            return;
        }

        var actor = userContext.ToAuditActor();
        foreach (var entry in entries)
        {
            if (AuditEntryFactory.Create(entry, actor, now) is { } auditLog)
            {
                context.Set<AuditLog>().Add(auditLog);
            }
        }
    }

    private void DispatchIfCommitted(DbContext? context)
    {
        if (context?.Database.CurrentTransaction is not null)
        {
            return; // the transaction interceptor flushes at commit
        }

        committedChanges.Flush();
        if (signal.TryConsume())
        {
            dispatcher.TriggerProcessing();
        }
    }

    private void ResetIfNoTransaction(DbContext? context)
    {
        if (context?.Database.CurrentTransaction is null)
        {
            signal.Reset();
            committedChanges.Reset();
        }
    }
}
