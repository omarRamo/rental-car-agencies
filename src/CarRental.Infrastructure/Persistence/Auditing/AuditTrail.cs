using CarRental.Application.Common.Auditing;
using CarRental.Application.Common.Security;
using CarRental.Domain.Identity;

namespace CarRental.Infrastructure.Persistence.Auditing;

internal sealed class AuditTrail(CarRentalDbContext db, IUserContext userContext, TimeProvider timeProvider) : IAuditTrail
{
    public async Task RecordAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);

        var actor = userContext.ToAuditActor();
        if (auditEvent.ActingUserId is not null || auditEvent.ActingUserName is not null)
        {
            actor = actor with
            {
                UserId = auditEvent.ActingUserId ?? actor.UserId,
                UserName = auditEvent.ActingUserName ?? actor.UserName,
            };
        }

        db.Set<AuditLog>().Add(AuditLog.Record(
            timeProvider.GetUtcNow(),
            actor,
            auditEvent.Action,
            auditEvent.EntityType,
            auditEvent.EntityId,
            details: auditEvent.Details,
            agencyId: auditEvent.AgencyId ?? userContext.AgencyId));
        await db.SaveChangesAsync(cancellationToken);
    }
}

internal sealed class DocumentAccessLogger(CarRentalDbContext db, IUserContext userContext, TimeProvider timeProvider) : IDocumentAccessLog
{
    public async Task RecordAsync(
        Guid documentId,
        string documentKind,
        string access,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        db.Set<DocumentAccessLog>().Add(DocumentAccessLog.Record(
            timeProvider.GetUtcNow(),
            userContext.ToAuditActor(),
            documentId,
            documentKind,
            access,
            reason,
            userContext.AgencyId));
        await db.SaveChangesAsync(cancellationToken);
    }
}
