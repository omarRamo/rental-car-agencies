using CarRental.Domain.Common;
using CarRental.Domain.Fleet;
using CarRental.Domain.Referential;
using CarRental.Infrastructure.Persistence.Auditing;
using CarRental.Infrastructure.Persistence.Conventions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CarRental.Infrastructure.Persistence.Configurations.Fleet;

/// <summary>
/// The vehicle aggregate: root in a temporal table, children in plain tables of the <c>fleet</c> schema (their history
/// is kept by the aggregate itself: superseded documents, completed maintenances, history lines, audit log).
/// </summary>
internal sealed class VehicleConfiguration : IEntityTypeConfiguration<Vehicle>
{
    public void Configure(EntityTypeBuilder<Vehicle> builder)
    {
        builder.ToTemporalTable("Vehicles", Schemas.Fleet);
        builder.HasKey(v => v.Id);

        // Unicode: plates may carry Arabic letters (تونس).
        builder.Property(v => v.PlateNumber).HasMaxLength(Vehicle.PlateNumberMaxLength).IsRequired();
        builder.Property(v => v.Make).HasMaxLength(Vehicle.MakeMaxLength).IsRequired();
        builder.Property(v => v.Model).HasMaxLength(Vehicle.ModelMaxLength).IsRequired();
        builder.Property(v => v.Color).HasMaxLength(Vehicle.ColorMaxLength);
        builder.Property(v => v.StatusReason).HasMaxLength(Vehicle.ReasonMaxLength);
        builder.Property(v => v.Buyer).HasMaxLength(Vehicle.BuyerMaxLength);

        // Technical counter bumped by every change, so that the rowversion of the root guards child changes too.
        builder.Property(v => v.Revision).IsAuditIgnored();

        builder.HasOne<Agency>().WithMany().HasForeignKey(v => v.AgencyId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<VehicleCategory>().WithMany().HasForeignKey(v => v.CategoryId).OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(v => v.Documents).WithOne().HasForeignKey(d => d.VehicleId).OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(v => v.MaintenancePlans).WithOne().HasForeignKey(p => p.VehicleId).OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(v => v.MaintenanceEvents).WithOne().HasForeignKey(e => e.VehicleId).OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(v => v.Costs).WithOne().HasForeignKey(c => c.VehicleId).OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(v => v.Photos).WithOne().HasForeignKey(p => p.VehicleId).OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(v => v.History).WithOne().HasForeignKey(h => h.VehicleId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(v => v.Economics).WithOne().HasForeignKey<VehicleEconomics>(e => e.VehicleId).OnDelete(DeleteBehavior.Cascade);

        builder.Ignore(v => v.CurrentDocuments);
        builder.Ignore(v => v.CompliantUntil);
        builder.Ignore(v => v.HasMaintenanceInProgress);

        // A plate is unique within an agency, sold vehicles included (the plate stays with the car).
        builder.HasIndex(v => new { v.AgencyId, v.PlateNumber }).IsUnique();
        builder.HasIndex(v => new { v.CategoryId, v.Status });
    }
}

internal sealed class VehicleDocumentConfiguration : IEntityTypeConfiguration<VehicleDocument>
{
    public void Configure(EntityTypeBuilder<VehicleDocument> builder)
    {
        builder.ToTable("VehicleDocuments", Schemas.Fleet);
        builder.HasKey(d => d.Id);
        builder.Property(d => d.Number).HasMaxLength(VehicleDocument.NumberMaxLength).IsRequired();
        builder.Property(d => d.Provider).HasMaxLength(VehicleDocument.ProviderMaxLength);
        builder.Property(d => d.Notes).HasMaxLength(VehicleDocument.NotesMaxLength);
        builder.Ignore(d => d.ValidUntil);

        // One current document per type is an invariant of the aggregate (guarded by the root rowversion).
        builder.HasIndex(d => new { d.VehicleId, d.Type, d.IsCurrent });
        builder.HasIndex(d => d.ExpiresOn).HasFilter("[IsCurrent] = 1");
    }
}

internal sealed class MaintenancePlanConfiguration : IEntityTypeConfiguration<MaintenancePlan>
{
    public void Configure(EntityTypeBuilder<MaintenancePlan> builder)
    {
        builder.ToTable("MaintenancePlans", Schemas.Fleet);
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Label).HasMaxLength(MaintenancePlan.LabelMaxLength);
        builder.Ignore(p => p.NextDueMileage);
        builder.Ignore(p => p.NextDueOn);
        builder.HasIndex(p => new { p.VehicleId, p.Type }).IsUnique();
    }
}

internal sealed class MaintenanceEventConfiguration : IEntityTypeConfiguration<MaintenanceEvent>
{
    public void Configure(EntityTypeBuilder<MaintenanceEvent> builder)
    {
        builder.ToTable("MaintenanceEvents", Schemas.Fleet);
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Description).HasMaxLength(MaintenanceEvent.DescriptionMaxLength);
        builder.Property(e => e.Provider).HasMaxLength(MaintenanceEvent.ProviderMaxLength);
        builder.Property(e => e.CancellationReason).HasMaxLength(Vehicle.ReasonMaxLength);
        builder.Property(e => e.CompletionNotes).HasMaxLength(Vehicle.ReasonMaxLength);
        builder.HasIndex(e => new { e.VehicleId, e.StartsAtUtc });
    }
}

internal sealed class VehicleEconomicsConfiguration : IEntityTypeConfiguration<VehicleEconomics>
{
    public void Configure(EntityTypeBuilder<VehicleEconomics> builder)
    {
        builder.ToTable("VehicleEconomics", Schemas.Fleet);
        builder.HasKey(e => e.Id);
        builder.Ignore(e => e.DepreciableAmount);
        builder.Ignore(e => e.DepreciationEndsOn);
        builder.HasIndex(e => e.VehicleId).IsUnique();
    }
}

internal sealed class VehicleCostConfiguration : IEntityTypeConfiguration<VehicleCost>
{
    public void Configure(EntityTypeBuilder<VehicleCost> builder)
    {
        builder.ToTable("VehicleCosts", Schemas.Fleet);
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Label).HasMaxLength(VehicleCost.LabelMaxLength).IsRequired();
        builder.HasIndex(c => new { c.VehicleId, c.IncurredOn });
    }
}

internal sealed class VehiclePhotoConfiguration : IEntityTypeConfiguration<VehiclePhoto>
{
    public void Configure(EntityTypeBuilder<VehiclePhoto> builder)
    {
        builder.ToTable("VehiclePhotos", Schemas.Fleet);
        builder.HasKey(p => p.Id);
        builder.Property(p => p.StoragePath).HasMaxLength(StoredPhoto.PathMaxLength).IsUnicode(false).IsRequired();
        builder.Property(p => p.ContentType).HasMaxLength(StoredPhoto.ContentTypeMaxLength).IsUnicode(false).IsRequired();
        builder.Property(p => p.ContentHash).HasMaxLength(StoredPhoto.ContentHashLength).IsUnicode(false).IsFixedLength().IsRequired();
        builder.HasIndex(p => p.VehicleId);
    }
}

internal sealed class VehicleHistoryEntryConfiguration : IEntityTypeConfiguration<VehicleHistoryEntry>
{
    public void Configure(EntityTypeBuilder<VehicleHistoryEntry> builder)
    {
        builder.ToTable("VehicleHistory", Schemas.Fleet);

        // The history is itself a trail of the vehicle changes, already audited on the vehicle: no duplicate rows.
        builder.ExcludeFromAudit();
        builder.HasKey(h => h.Id);
        builder.Property(h => h.Actor).HasMaxLength(AuditActor.UserNameMaxLength);
        builder.Property(h => h.Subject).HasMaxLength(VehicleHistoryEntry.SubjectMaxLength);
        builder.Property(h => h.PreviousValue).HasMaxLength(VehicleHistoryEntry.ValueMaxLength);
        builder.Property(h => h.NewValue).HasMaxLength(VehicleHistoryEntry.ValueMaxLength);
        builder.Property(h => h.Reason).HasMaxLength(Vehicle.ReasonMaxLength);
        builder.HasIndex(h => new { h.VehicleId, h.OccurredAtUtc });
    }
}

internal sealed class DeadlineAlertConfiguration : IEntityTypeConfiguration<DeadlineAlert>
{
    public void Configure(EntityTypeBuilder<DeadlineAlert> builder)
    {
        builder.ToTable("DeadlineAlerts", Schemas.Fleet);

        // Notification bookkeeping of the daily job (the e-mails themselves go through the outbox).
        builder.ExcludeFromAudit();
        builder.HasKey(a => a.Id);
        builder.HasOne<VehicleDocument>().WithMany().HasForeignKey(a => a.DocumentId).OnDelete(DeleteBehavior.Restrict);

        // Each (document, level) is notified once, even if two runs overlapped.
        builder.HasIndex(a => new { a.DocumentId, a.Level }).IsUnique();
        builder.HasIndex(a => a.VehicleId);
    }
}
