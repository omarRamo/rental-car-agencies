using CarRental.Application.Common.Security;
using CarRental.Domain.Common;
using CarRental.Domain.Identity;
using CarRental.Domain.Referential;
using CarRental.Infrastructure.Identity;
using CarRental.Infrastructure.Outbox;
using CarRental.Infrastructure.Persistence.Auditing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace CarRental.Infrastructure.Persistence.Interceptors;

/// <summary>
/// The persistence pipeline run before every save, in this order:
/// <list type="number">
/// <item>agency guard (ADR-0067): an operation that works for one agency only writes the rows of that agency, and no
/// row ever moves to another agency;</item>
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

    /// <summary>
    /// Refuses the save when a written row belongs to another agency than the one the operation works for, when a row
    /// would move to another agency, when agency data has no agency, or when a platform row (shared value, agency
    /// registration) is written by an operation that does not work for the whole platform — the support inside an
    /// agency or a system step working for one agency included: only an operation across agencies writes them. Records
    /// of an agency or of the platform (<see cref="IAgencyOrPlatform"/>, the audit journals) are written by the
    /// platform's own code for the agency they concern — a sign-in, still anonymous, is recorded for the agency of the
    /// account — and never change.
    /// </summary>
    private void EnforceAgencyBoundaries(DbContext context)
    {
        var restricted = !userContext.IsAcrossAgencies;
        var agencyId = userContext.AgencyId;
        foreach (var entry in context.ChangeTracker.Entries())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted))
            {
                continue;
            }

            switch (entry.Entity)
            {
                case IAgencyOwned owned:
                    if (owned.AgencyId == Guid.Empty)
                    {
                        throw AgencyBoundaryException.Missing(entry.Metadata.ClrType);
                    }

                    EnsureNotMoved(entry, nameof(IAgencyOwned.AgencyId));
                    if (restricted && owned.AgencyId != agencyId)
                    {
                        throw AgencyBoundaryException.OtherAgency(entry.Metadata.ClrType);
                    }

                    break;
                case IAgencyShared shared:
                    EnsureNotMoved(entry, nameof(IAgencyShared.AgencyId));
                    // A shared value (no agency) is the platform's: an operation limited to an agency never writes it.
                    if (restricted && (shared.AgencyId is null || shared.AgencyId != agencyId))
                    {
                        throw AgencyBoundaryException.OtherAgency(entry.Metadata.ClrType);
                    }

                    break;
                case IAgencyOrPlatform:
                    EnsureNotMoved(entry, nameof(IAgencyOrPlatform.AgencyId));
                    break;
                case Agency agency:
                    if (restricted && (entry.State != EntityState.Modified || agency.Id != agencyId))
                    {
                        throw AgencyBoundaryException.OtherAgency(entry.Metadata.ClrType);
                    }

                    break;
            }
        }
    }

    private static void EnsureNotMoved(Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry entry, string propertyName)
    {
        if (entry.State == EntityState.Modified
            && !Equals(entry.Property(propertyName).OriginalValue, entry.Property(propertyName).CurrentValue))
        {
            throw AgencyBoundaryException.Moved(entry.Metadata.ClrType);
        }
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

        EnforceAgencyBoundaries(context);
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
        var users = new Dictionary<Guid, Guid?>();
        foreach (var entry in entries)
        {
            if (AuditEntryFactory.Create(entry, actor, now, userContext.AgencyId, userId => AgencyOfUser(context, users, userId)) is { } auditLog)
            {
                context.Set<AuditLog>().Add(auditLog);
            }
        }
    }

    /// <summary>
    /// The agency of the account whose Identity rows are saved (ADR-0067): the tracked account when the save carries it,
    /// else the database (users are a platform table, never filtered by agency); read once per user and save.
    /// </summary>
    private static Guid? AgencyOfUser(DbContext context, Dictionary<Guid, Guid?> known, Guid userId)
    {
        if (!known.TryGetValue(userId, out var agencyId))
        {
            agencyId = context.ChangeTracker.Entries<ApplicationUser>().FirstOrDefault(e => e.Entity.Id == userId) is { } tracked
                ? tracked.Entity.AgencyId
                : context.Set<ApplicationUser>().AsNoTracking().Where(u => u.Id == userId).Select(u => u.AgencyId).FirstOrDefault();
            known[userId] = agencyId;
        }

        return agencyId;
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
