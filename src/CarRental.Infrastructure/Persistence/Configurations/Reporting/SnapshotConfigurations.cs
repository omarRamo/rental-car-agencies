using CarRental.Domain.Referential;
using CarRental.Domain.Reporting;
using CarRental.Infrastructure.Persistence.Auditing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CarRental.Infrastructure.Persistence.Configurations.Reporting;

/// <summary>
/// Daily snapshots of the vehicles (<c>reporting.VehicleDaySnapshots</c>, ADR-0045): derived data replaced day by day,
/// hence neither temporal nor audited (the recomputation itself is an audit event). One row per agency, day and vehicle
/// (unique index: two computations of the same day cannot both insert); the profitability of a vehicle reads its days.
/// </summary>
internal sealed class VehicleDaySnapshotConfiguration : IEntityTypeConfiguration<VehicleDaySnapshot>
{
    public void Configure(EntityTypeBuilder<VehicleDaySnapshot> builder)
    {
        builder.ToTable("VehicleDaySnapshots", Schemas.Reporting);
        builder.ExcludeFromAudit();
        builder.HasKey(s => s.Id);
        builder.Property(s => s.PlateNumber).HasMaxLength(VehicleDaySnapshot.PlateNumberMaxLength).IsRequired();
        builder.Property(s => s.VehicleName).HasMaxLength(VehicleDaySnapshot.VehicleNameMaxLength).IsRequired();
        builder.Property(s => s.CategoryCode).HasMaxLength(VehicleDaySnapshot.CategoryCodeLength).IsFixedLength().IsUnicode(false).IsRequired();
        builder.Property(s => s.BookingNumber).HasMaxLength(VehicleDaySnapshot.BookingNumberMaxLength).IsUnicode(false);
        builder.Ignore(s => s.Margin);

        builder.HasOne<Agency>().WithMany().HasForeignKey(s => s.AgencyId).OnDelete(DeleteBehavior.Restrict);

        // The shares of the rentals go with their row when a day is replaced.
        builder.HasMany(s => s.Rentals).WithOne().HasForeignKey(r => r.SnapshotId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(s => s.Rentals).HasField("_rentals").UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.HasIndex(s => new { s.AgencyId, s.Day, s.VehicleId }).IsUnique();
        builder.HasIndex(s => new { s.AgencyId, s.VehicleId, s.Day });
    }
}

/// <summary>The shares of the rentals in the vehicle-days (<c>reporting.VehicleDayRentals</c>): read by period for the channels.</summary>
internal sealed class VehicleDayRentalConfiguration : IEntityTypeConfiguration<VehicleDayRental>
{
    public void Configure(EntityTypeBuilder<VehicleDayRental> builder)
    {
        builder.ToTable("VehicleDayRentals", Schemas.Reporting);
        builder.ExcludeFromAudit();
        builder.HasKey(r => r.Id);
        builder.Property(r => r.BookingNumber).HasMaxLength(VehicleDaySnapshot.BookingNumberMaxLength).IsUnicode(false).IsRequired();
        builder.HasIndex(r => new { r.AgencyId, r.Day });
        builder.HasIndex(r => r.BookingId);
    }
}

/// <summary>The computed days of the snapshots of each agency (<c>reporting.SnapshotDays</c>): one per agency and day.</summary>
internal sealed class SnapshotDayConfiguration : IEntityTypeConfiguration<SnapshotDay>
{
    public void Configure(EntityTypeBuilder<SnapshotDay> builder)
    {
        builder.ToTable("SnapshotDays", Schemas.Reporting);
        builder.ExcludeFromAudit();
        builder.HasKey(d => d.Id);
        builder.HasOne<Agency>().WithMany().HasForeignKey(d => d.AgencyId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(d => new { d.AgencyId, d.Day }).IsUnique();
    }
}
