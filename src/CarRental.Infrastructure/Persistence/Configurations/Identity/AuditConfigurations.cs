using CarRental.Domain.Common;
using CarRental.Domain.Identity;
using CarRental.Infrastructure.Persistence.Auditing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CarRental.Infrastructure.Persistence.Configurations.Identity;

internal sealed class AuditLogConfiguration : IEntityTypeConfiguration<AuditLog>
{
    /// <summary>Database trigger rejecting UPDATE and DELETE (created by the InitialCreate migration).</summary>
    public const string AppendOnlyTrigger = "TR_AuditLogs_AppendOnly";

    public void Configure(EntityTypeBuilder<AuditLog> builder)
    {
        // Declaring the trigger makes EF Core avoid the OUTPUT clause, incompatible with triggers.
        builder.ToTable("AuditLogs", Schemas.Audit, table => table.HasTrigger(AppendOnlyTrigger));
        builder.ExcludeFromAudit();
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).UseIdentityColumn();
        builder.Property(a => a.Action).HasMaxLength(AuditLog.ActionMaxLength).IsUnicode(false).IsRequired();
        builder.Property(a => a.UserName).HasMaxLength(AuditActor.UserNameMaxLength);
        builder.Property(a => a.IpAddress).HasMaxLength(AuditActor.IpAddressMaxLength).IsUnicode(false);
        builder.Property(a => a.CorrelationId).HasMaxLength(AuditActor.CorrelationIdMaxLength).IsUnicode(false);
        builder.Property(a => a.EntityType).HasMaxLength(AuditLog.EntityTypeMaxLength);
        builder.Property(a => a.EntityId).HasMaxLength(AuditLog.EntityIdMaxLength);
        builder.Property(a => a.Details).HasMaxLength(AuditLog.DetailsMaxLength);

        builder.HasIndex(a => a.OccurredAtUtc);
        builder.HasIndex(a => new { a.AgencyId, a.OccurredAtUtc });
        builder.HasIndex(a => new { a.EntityType, a.EntityId });
        builder.HasIndex(a => a.UserId);
        builder.HasIndex(a => a.Action);
        builder.HasIndex(a => a.CorrelationId);
    }
}

internal sealed class DocumentAccessLogConfiguration : IEntityTypeConfiguration<DocumentAccessLog>
{
    public const string AppendOnlyTrigger = "TR_DocumentAccessLogs_AppendOnly";

    public void Configure(EntityTypeBuilder<DocumentAccessLog> builder)
    {
        builder.ToTable("DocumentAccessLogs", Schemas.Audit, table => table.HasTrigger(AppendOnlyTrigger));
        builder.ExcludeFromAudit();
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).UseIdentityColumn();
        builder.Property(a => a.UserName).HasMaxLength(AuditActor.UserNameMaxLength);
        builder.Property(a => a.IpAddress).HasMaxLength(AuditActor.IpAddressMaxLength).IsUnicode(false);
        builder.Property(a => a.CorrelationId).HasMaxLength(AuditActor.CorrelationIdMaxLength).IsUnicode(false);
        builder.Property(a => a.DocumentKind).HasMaxLength(DocumentAccessLog.DocumentKindMaxLength).IsUnicode(false).IsRequired();
        builder.Property(a => a.Access).HasMaxLength(DocumentAccessLog.AccessMaxLength).IsUnicode(false).IsRequired();
        builder.Property(a => a.Reason).HasMaxLength(DocumentAccessLog.ReasonMaxLength);

        builder.HasIndex(a => a.DocumentId);
        builder.HasIndex(a => a.OccurredAtUtc);
        builder.HasIndex(a => new { a.AgencyId, a.OccurredAtUtc });
    }
}
