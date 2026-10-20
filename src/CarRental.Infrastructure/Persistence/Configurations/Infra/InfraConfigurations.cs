using CarRental.Infrastructure.Outbox;
using CarRental.Infrastructure.Persistence.Auditing;
using CarRental.Infrastructure.Persistence.Numbering;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CarRental.Infrastructure.Persistence.Configurations.Infra;

internal sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        builder.ToTable("OutboxMessages", Schemas.Infra);
        builder.ExcludeFromAudit();
        builder.HasKey(m => m.Id);
        builder.Property(m => m.Id).ValueGeneratedNever();
        builder.Property(m => m.Type).HasMaxLength(OutboxMessage.TypeMaxLength).IsUnicode(false).IsRequired();
        builder.Property(m => m.Payload).IsRequired();
        builder.Property(m => m.CorrelationId).HasMaxLength(OutboxMessage.CorrelationIdMaxLength).IsUnicode(false);
        builder.Property(m => m.LastError).HasMaxLength(OutboxMessage.ErrorMaxLength);

        // Pending messages, in order.
        builder.HasIndex(m => new { m.OccurredAtUtc, m.NextAttemptAtUtc })
            .HasFilter("[ProcessedAtUtc] IS NULL")
            .HasDatabaseName("IX_OutboxMessages_Pending");
    }
}

internal sealed class DocumentCounterConfiguration : IEntityTypeConfiguration<DocumentCounter>
{
    public void Configure(EntityTypeBuilder<DocumentCounter> builder)
    {
        builder.ToTable("DocumentCounters", Schemas.Infra);
        builder.ExcludeFromAudit();
        builder.HasKey(c => new { c.DocumentType, c.Year });
        builder.Property(c => c.DocumentType).HasConversion<string>().HasMaxLength(32).IsUnicode(false);
    }
}
