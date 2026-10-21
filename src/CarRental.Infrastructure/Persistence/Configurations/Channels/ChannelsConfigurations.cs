using CarRental.Domain.Channels;
using CarRental.Domain.Common;
using CarRental.Domain.Referential;
using CarRental.Infrastructure.Persistence.Auditing;
using CarRental.Infrastructure.Persistence.Conventions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CarRental.Infrastructure.Persistence.Configurations.Channels;

/// <summary>
/// Channels (temporal table <c>channels.Channels</c>): code unique on the platform (the inbound address segment), one
/// channel per agency and sales channel, the hash of the API key masked in the audit log, mappings always loaded with
/// the channel. Other modules are referenced by id only (agency foreign key, ADR-0012).
/// </summary>
internal sealed class ChannelConfiguration : IEntityTypeConfiguration<Channel>
{
    public void Configure(EntityTypeBuilder<Channel> builder)
    {
        builder.ToTemporalTable("Channels", Schemas.Channels);
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Code).HasMaxLength(ChannelCodes.ChannelCodeMaxLength).IsUnicode(false).IsRequired();
        builder.Property(c => c.Name).HasMaxLength(Channel.NameMaxLength).IsRequired();
        builder.Property(c => c.CommissionRate).HasPrecision(5, 4);
        builder.Property(c => c.ApiKeyHash).HasMaxLength(ChannelApiKey.HashLength).IsUnicode(false).IsAuditSensitive();
        builder.Property(c => c.ApiKeyHint).HasMaxLength(ChannelApiKey.HintLength).IsUnicode(false);
        builder.Property(c => c.ClientCertificateThumbprint).HasMaxLength(CertificateThumbprints.Sha256Length).IsUnicode(false);
        builder.Property(c => c.AllowedIpRanges).HasMaxLength(IpAllowlist.StorageMaxLength).IsUnicode(false).IsRequired();
        builder.Property(c => c.UpdatedBy).HasMaxLength(AuditActor.UserNameMaxLength);

        builder.Ignore(c => c.IpAllowlist);
        builder.Ignore(c => c.HasInboundCredentials);

        builder.HasOne<Agency>().WithMany().HasForeignKey(c => c.AgencyId).OnDelete(DeleteBehavior.Restrict);
        builder.HasMany(c => c.Mappings).WithOne().HasForeignKey(m => m.ChannelId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(c => c.Mappings).HasField("_mappings").UsePropertyAccessMode(PropertyAccessMode.Field).AutoInclude();

        builder.HasIndex(c => c.Code).IsUnique();
        builder.HasIndex(c => new { c.AgencyId, c.SalesChannel }).IsUnique();
    }
}

/// <summary>
/// Mappings of a channel: one per category or location (by id) and per extra code, one internal target per channel
/// code and kind (unique indexes; the default collation makes the channel codes case-insensitive, like the domain).
/// </summary>
internal sealed class ChannelMappingConfiguration : IEntityTypeConfiguration<ChannelMapping>
{
    public void Configure(EntityTypeBuilder<ChannelMapping> builder)
    {
        builder.ToTable("ChannelMappings", Schemas.Channels);
        builder.HasKey(m => m.Id);
        builder.Property(m => m.ChannelCode).HasMaxLength(ChannelCodes.MappingCodeMaxLength).IsRequired();
        builder.Property(m => m.InternalCode).HasMaxLength(ChannelCodes.InternalCodeMaxLength).IsUnicode(false);
        builder.Property(m => m.AcrissCode).HasMaxLength(4).IsUnicode(false);
        builder.Ignore(m => m.IsById);

        builder.HasIndex(m => new { m.ChannelId, m.Kind, m.InternalId }).IsUnique().HasFilter("[InternalId] IS NOT NULL");
        builder.HasIndex(m => new { m.ChannelId, m.Kind, m.InternalCode }).IsUnique().HasFilter("[InternalCode] IS NOT NULL");
        builder.HasIndex(m => new { m.ChannelId, m.Kind, m.ChannelCode }).IsUnique();
        builder.HasIndex(m => new { m.Kind, m.InternalId });
    }
}

/// <summary>
/// Quotas and stop-sells (temporal table, soft-deleted): read by the availability policy by category, sales channel
/// and end date.
/// </summary>
internal sealed class ChannelQuotaConfiguration : IEntityTypeConfiguration<ChannelQuota>
{
    public void Configure(EntityTypeBuilder<ChannelQuota> builder)
    {
        builder.ToTemporalTable("ChannelQuotas", Schemas.Channels);
        builder.HasKey(q => q.Id);
        builder.Property(q => q.Note).HasMaxLength(ChannelQuota.NoteMaxLength);
        builder.Property(q => q.CreatedBy).HasMaxLength(AuditActor.UserNameMaxLength);
        builder.Property(q => q.UpdatedBy).HasMaxLength(AuditActor.UserNameMaxLength);
        builder.Ignore(q => q.Period);

        builder.HasOne<Channel>().WithMany().HasForeignKey(q => q.ChannelId).OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(q => new { q.CategoryId, q.SalesChannel, q.EndDate });
        builder.HasIndex(q => new { q.ChannelId, q.EndDate });
    }
}

/// <summary>
/// The channel side of the rental files (temporal table): one per rental file, one per channel reference (the
/// idempotency key of the channel's messages), indexes of the reconciliation and of the commissions by pick-up.
/// </summary>
internal sealed class ChannelBookingConfiguration : IEntityTypeConfiguration<ChannelBooking>
{
    public void Configure(EntityTypeBuilder<ChannelBooking> builder)
    {
        builder.ToTemporalTable("ChannelBookings", Schemas.Channels);
        builder.HasKey(b => b.Id);
        builder.Property(b => b.ChannelReference).HasMaxLength(ChannelCodes.ReferenceMaxLength).IsRequired();
        builder.Property(b => b.BookingNumber).HasMaxLength(ChannelBooking.BookingNumberMaxLength).IsUnicode(false).IsRequired();
        builder.Property(b => b.RequestFingerprint).HasMaxLength(ChannelBooking.FingerprintLength).IsFixedLength().IsUnicode(false).IsRequired();
        builder.Property(b => b.CommissionRate).HasPrecision(5, 4);

        builder.HasOne<Channel>().WithMany().HasForeignKey(b => b.ChannelId).OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(b => new { b.ChannelId, b.ChannelReference }).IsUnique();
        builder.HasIndex(b => b.BookingId).IsUnique();
        builder.HasIndex(b => new { b.ChannelId, b.PickupAt });
        builder.HasIndex(b => new { b.AgencyId, b.PickupAt });
    }
}

/// <summary>The message log (appended only, purged after its retention): a journal itself, left out of the audit log.</summary>
internal sealed class ChannelMessageLogConfiguration : IEntityTypeConfiguration<ChannelMessageLog>
{
    public void Configure(EntityTypeBuilder<ChannelMessageLog> builder)
    {
        builder.ToTable("ChannelMessageLogs", Schemas.Channels);
        builder.ExcludeFromAudit();
        builder.HasKey(m => m.Id);
        builder.Property(m => m.ChannelCode).HasMaxLength(ChannelMessageLog.ChannelCodeMaxLength).IsUnicode(false).IsRequired();
        builder.Property(m => m.ChannelReference).HasMaxLength(ChannelCodes.ReferenceMaxLength);
        builder.Property(m => m.Excerpt).HasMaxLength(ChannelMessageLog.ExcerptMaxLength);
        builder.Property(m => m.ErrorCode).HasMaxLength(ChannelMessageLog.ErrorCodeMaxLength).IsUnicode(false);
        builder.Property(m => m.RemoteAddress).HasMaxLength(ChannelMessageLog.RemoteAddressMaxLength).IsUnicode(false);
        builder.Property(m => m.CorrelationId).HasMaxLength(ChannelMessageLog.CorrelationIdMaxLength).IsUnicode(false);

        builder.HasIndex(m => m.OccurredAtUtc);
        builder.HasIndex(m => new { m.ChannelId, m.OccurredAtUtc });
        builder.HasIndex(m => m.ChannelReference);
    }
}

/// <summary>The outbound queue (technical, like the outbox: left out of the audit log), read by status and due time.</summary>
internal sealed class ChannelSyncItemConfiguration : IEntityTypeConfiguration<ChannelSyncItem>
{
    public void Configure(EntityTypeBuilder<ChannelSyncItem> builder)
    {
        builder.ToTable("ChannelSyncItems", Schemas.Channels);
        builder.ExcludeFromAudit();
        builder.HasKey(i => i.Id);
        builder.Property(i => i.CoalesceKey).HasMaxLength(ChannelSyncItem.CoalesceKeyMaxLength).IsUnicode(false).IsRequired();
        builder.Property(i => i.Reason).HasMaxLength(ChannelSyncItem.ReasonMaxLength).IsUnicode(false).IsRequired();
        builder.Property(i => i.LastError).HasMaxLength(ChannelSyncItem.ErrorMaxLength);

        builder.HasOne<Channel>().WithMany().HasForeignKey(i => i.ChannelId).OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(i => new { i.Status, i.NextAttemptAtUtc });
        builder.HasIndex(i => i.CoalesceKey);
        builder.HasIndex(i => new { i.ChannelId, i.CreatedAtUtc });
    }
}

/// <summary>Changes to replicate on the extranet of a manual channel, read by channel until acknowledged.</summary>
internal sealed class ChannelReplicationItemConfiguration : IEntityTypeConfiguration<ChannelReplicationItem>
{
    public void Configure(EntityTypeBuilder<ChannelReplicationItem> builder)
    {
        builder.ToTable("ChannelReplicationItems", Schemas.Channels);
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Detail).HasMaxLength(ChannelReplicationItem.DetailMaxLength);
        builder.Property(r => r.CreatedBy).HasMaxLength(AuditActor.UserNameMaxLength);
        builder.Property(r => r.AcknowledgedBy).HasMaxLength(AuditActor.UserNameMaxLength);
        builder.Ignore(r => r.IsAcknowledged);

        builder.HasOne<Channel>().WithMany().HasForeignKey(r => r.ChannelId).OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(r => new { r.ChannelId, r.AcknowledgedAtUtc });
    }
}

/// <summary>Monthly payouts of the channels (temporal table): one per channel and month.</summary>
internal sealed class ChannelPayoutConfiguration : IEntityTypeConfiguration<ChannelPayout>
{
    public void Configure(EntityTypeBuilder<ChannelPayout> builder)
    {
        builder.ToTemporalTable("ChannelPayouts", Schemas.Channels);
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Reference).HasMaxLength(ChannelPayout.ReferenceMaxLength);
        builder.Property(p => p.Note).HasMaxLength(ChannelPayout.NoteMaxLength);
        builder.Property(p => p.RecordedBy).HasMaxLength(AuditActor.UserNameMaxLength);

        builder.HasOne<Channel>().WithMany().HasForeignKey(p => p.ChannelId).OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(p => new { p.ChannelId, p.Year, p.Month }).IsUnique();
    }
}

/// <summary>What the outbound synchronization last saw of a channel (technical: left out of the audit log).</summary>
internal sealed class ChannelSyncStateConfiguration : IEntityTypeConfiguration<ChannelSyncState>
{
    public void Configure(EntityTypeBuilder<ChannelSyncState> builder)
    {
        builder.ToTable("ChannelSyncStates", Schemas.Channels);
        builder.ExcludeFromAudit();
        builder.HasKey(s => s.Id);
        builder.Property(s => s.RatesFingerprint).HasMaxLength(ChannelSyncState.FingerprintMaxLength).IsUnicode(false);
        builder.HasOne<Channel>().WithOne().HasForeignKey<ChannelSyncState>(s => s.Id).OnDelete(DeleteBehavior.Cascade);
    }
}
