using System.Globalization;
using CarRental.Domain.Pricing;
using CarRental.Domain.Referential;
using CarRental.Infrastructure.Persistence.Auditing;
using CarRental.Infrastructure.Persistence.Conventions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CarRental.Infrastructure.Persistence.Configurations.Pricing;

/// <summary>
/// Rate plans and their content, schema <c>pricing</c>: one system-versioned table per part (ADR-0003, ADR-0023), so the
/// state of a plan at any date can be rebuilt; published versions stay as rows and quotes name the version they used.
/// Every row carries the agency of its plan (ADR-0067); the content is read through its plan, so only the plan has the
/// foreign key to the agency.
/// </summary>
internal sealed class RatePlanConfiguration : IEntityTypeConfiguration<RatePlan>
{
    public void Configure(EntityTypeBuilder<RatePlan> builder)
    {
        builder.ToTemporalTable("RatePlans", Schemas.Pricing);
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Name).HasMaxLength(RatePlan.NameMaxLength).IsRequired();
        builder.Property(p => p.Revision).IsAuditIgnored();
        builder.HasOne<Agency>().WithMany().HasForeignKey(p => p.AgencyId).OnDelete(DeleteBehavior.Restrict);

        // One published plan and one draft per agency and channel (the default plan has a NULL channel, which SQL Server
        // treats as one value in a unique index), and a version number used once per agency and channel.
        builder.HasIndex(p => new { p.AgencyId, p.Channel }, "IX_RatePlans_Published")
            .IsUnique()
            .HasFilter(StatusFilter(RatePlanStatus.Published));
        builder.HasIndex(p => new { p.AgencyId, p.Channel }, "IX_RatePlans_Draft")
            .IsUnique()
            .HasFilter(StatusFilter(RatePlanStatus.Draft));
        builder.HasIndex(p => new { p.AgencyId, p.Channel, p.Version }, "IX_RatePlans_Version")
            .IsUnique()
            .HasFilter("[Version] IS NOT NULL");

        builder.HasMany(p => p.Seasons).WithOne().HasForeignKey(s => s.RatePlanId).OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(p => p.Events).WithOne().HasForeignKey(e => e.RatePlanId).OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(p => p.Prices).WithOne().HasForeignKey(p => p.RatePlanId).OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(p => p.Tiers).WithOne().HasForeignKey(t => t.RatePlanId).OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(p => p.TierCategoryDiscounts).WithOne().HasForeignKey(d => d.RatePlanId).OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(p => p.Extras).WithOne().HasForeignKey(e => e.RatePlanId).OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(p => p.Promotions).WithOne().HasForeignKey(p => p.RatePlanId).OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(p => p.ChannelRules).WithOne().HasForeignKey(r => r.RatePlanId).OnDelete(DeleteBehavior.Cascade);

        // The collection getters return sorted copies: EF Core reads and writes the backing fields.
        builder.Navigation(p => p.Seasons).UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.Navigation(p => p.Events).UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.Navigation(p => p.Prices).UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.Navigation(p => p.Tiers).UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.Navigation(p => p.TierCategoryDiscounts).UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.Navigation(p => p.Extras).UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.Navigation(p => p.Promotions).UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.Navigation(p => p.ChannelRules).UsePropertyAccessMode(PropertyAccessMode.Field);
    }

    private static string StatusFilter(RatePlanStatus status) =>
        string.Create(CultureInfo.InvariantCulture, $"[Status] = {(int)status}");
}

internal sealed class SeasonConfiguration : IEntityTypeConfiguration<Season>
{
    public void Configure(EntityTypeBuilder<Season> builder)
    {
        builder.ToTemporalTable("RatePlanSeasons", Schemas.Pricing);
        builder.HasAuditName("RatePlanSeason");
        builder.HasKey(s => s.Id);
    }
}

internal sealed class PriorityEventConfiguration : IEntityTypeConfiguration<PriorityEvent>
{
    public void Configure(EntityTypeBuilder<PriorityEvent> builder)
    {
        builder.ToTemporalTable("RatePlanEvents", Schemas.Pricing);
        builder.HasAuditName("RatePlanEvent");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Name).HasMaxLength(PriorityEvent.NameMaxLength).IsRequired();
    }
}

internal sealed class CategoryPriceConfiguration : IEntityTypeConfiguration<CategoryPrice>
{
    public void Configure(EntityTypeBuilder<CategoryPrice> builder)
    {
        builder.ToTemporalTable("RatePlanPrices", Schemas.Pricing);
        builder.HasAuditName("RatePlanPrice");
        builder.HasKey(p => p.Id);

        // One price per category and grid column; NULL event ids (base and season columns) count as one value.
        builder.HasIndex(p => new { p.RatePlanId, p.CategoryId, p.PeriodKind, p.EventId }).IsUnique().HasFilter(null);
    }
}

internal sealed class DurationTierConfiguration : IEntityTypeConfiguration<DurationTier>
{
    public void Configure(EntityTypeBuilder<DurationTier> builder)
    {
        builder.ToTemporalTable("RatePlanTiers", Schemas.Pricing);
        builder.HasAuditName("RatePlanTier");
        builder.HasKey(t => t.Id);
        builder.Property(t => t.DiscountPercent).HasPrecision(5, 2);
        builder.HasIndex(t => new { t.RatePlanId, t.MinimumDays }).IsUnique();
    }
}

internal sealed class TierCategoryDiscountConfiguration : IEntityTypeConfiguration<TierCategoryDiscount>
{
    public void Configure(EntityTypeBuilder<TierCategoryDiscount> builder)
    {
        builder.ToTemporalTable("RatePlanTierDiscounts", Schemas.Pricing);
        builder.HasAuditName("RatePlanTierDiscount");
        builder.HasKey(d => d.Id);
        builder.Property(d => d.DiscountPercent).HasPrecision(5, 2);
        builder.HasIndex(d => new { d.RatePlanId, d.TierMinimumDays, d.CategoryId }).IsUnique();
    }
}

internal sealed class ExtraConfiguration : IEntityTypeConfiguration<Extra>
{
    public void Configure(EntityTypeBuilder<Extra> builder)
    {
        // Codes are unique per plan by the aggregate; no unique index, so that two extras can swap their codes in one save.
        builder.ToTemporalTable("RatePlanExtras", Schemas.Pricing);
        builder.HasAuditName("RatePlanExtra");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Code).HasMaxLength(Extra.CodeMaxLength).IsUnicode(false).IsRequired();
        builder.Property(e => e.Name).HasMaxLength(Extra.NameMaxLength).IsRequired();
        builder.Property(e => e.NameEn).HasMaxLength(Extra.NameMaxLength);
        builder.HasOne<Location>().WithMany().HasForeignKey(e => e.LocationId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class PromotionConfiguration : IEntityTypeConfiguration<Promotion>
{
    public void Configure(EntityTypeBuilder<Promotion> builder)
    {
        builder.ToTemporalTable("RatePlanPromotions", Schemas.Pricing);
        builder.HasAuditName("RatePlanPromotion");
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Name).HasMaxLength(Promotion.NameMaxLength).IsRequired();
        builder.Property(p => p.Code).HasMaxLength(Promotion.CodeMaxLength).IsUnicode(false);
        builder.Property(p => p.DiscountPercent).HasPrecision(5, 2);

        // Filters stored as JSON arrays (an empty array means every category or channel).
        builder.PrimitiveCollection(p => p.CategoryIds).IsRequired();
        builder.PrimitiveCollection(p => p.Channels).IsRequired();
    }
}

internal sealed class ChannelRuleConfiguration : IEntityTypeConfiguration<ChannelRule>
{
    public void Configure(EntityTypeBuilder<ChannelRule> builder)
    {
        builder.ToTemporalTable("RatePlanChannelRules", Schemas.Pricing);
        builder.HasAuditName("RatePlanChannelRule");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.MarkupPercent).HasPrecision(5, 2);
        builder.HasIndex(r => new { r.RatePlanId, r.Channel }).IsUnique();
    }
}
