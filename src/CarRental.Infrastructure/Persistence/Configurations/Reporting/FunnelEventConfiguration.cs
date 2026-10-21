using CarRental.Domain.Reporting;
using CarRental.Infrastructure.Persistence.Auditing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CarRental.Infrastructure.Persistence.Configurations.Reporting;

/// <summary>
/// Funnel measurement (<c>reporting.FunnelEvents</c>): append-only, anonymous, high volume, so neither temporal nor
/// audited; rows only leave through the retention purge.
/// </summary>
internal sealed class FunnelEventConfiguration : IEntityTypeConfiguration<FunnelEvent>
{
    public void Configure(EntityTypeBuilder<FunnelEvent> builder)
    {
        builder.ToTable("FunnelEvents", Schemas.Reporting);
        builder.ExcludeFromAudit();
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Culture).HasMaxLength(FunnelEvent.CultureMaxLength).IsUnicode(false).IsRequired();
        builder.Property(e => e.Source).HasMaxLength(FunnelEvent.SourceMaxLength).IsUnicode(false);
        builder.Property(e => e.UtmSource).HasMaxLength(FunnelEvent.UtmMaxLength);
        builder.Property(e => e.UtmMedium).HasMaxLength(FunnelEvent.UtmMaxLength);
        builder.Property(e => e.UtmCampaign).HasMaxLength(FunnelEvent.UtmMaxLength);
        builder.Property(e => e.UtmTerm).HasMaxLength(FunnelEvent.UtmMaxLength);
        builder.Property(e => e.UtmContent).HasMaxLength(FunnelEvent.UtmMaxLength);

        // Purge and period reports scan by time; per-visit analysis joins on the visit.
        builder.HasIndex(e => new { e.OccurredAtUtc, e.Step });
        builder.HasIndex(e => e.VisitId).HasFilter("[VisitId] IS NOT NULL");
    }
}
