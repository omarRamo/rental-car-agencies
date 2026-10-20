using CarRental.Domain.Fleet;
using CarRental.Domain.Referential;
using CarRental.Infrastructure.Persistence.Conventions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CarRental.Infrastructure.Persistence.Configurations.Fleet;

internal sealed class VehicleCategoryConfiguration : IEntityTypeConfiguration<VehicleCategory>
{
    public void Configure(EntityTypeBuilder<VehicleCategory> builder)
    {
        builder.ToTemporalTable("VehicleCategories", Schemas.Fleet);
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Code).IsRequired();
        builder.Property(c => c.Name).HasMaxLength(VehicleCategory.NameMaxLength).IsRequired();
        builder.Property(c => c.ExampleModel).HasMaxLength(VehicleCategory.ExampleModelMaxLength).IsRequired();
        builder.Property(c => c.PhotoPath).HasMaxLength(StoredPhoto.PathMaxLength).IsUnicode(false);
        builder.Property(c => c.PhotoContentType).HasMaxLength(StoredPhoto.ContentTypeMaxLength).IsUnicode(false);
        builder.Property(c => c.PhotoHash).HasMaxLength(StoredPhoto.ContentHashLength).IsUnicode(false).IsFixedLength();
        builder.HasOne<Agency>().WithMany().HasForeignKey(c => c.AgencyId).OnDelete(DeleteBehavior.Restrict);

        // An ACRISS code is unique among the live categories of an agency (a deleted category frees its code).
        builder.HasIndex(c => new { c.AgencyId, c.Code }).IsUnique().HasFilter("[IsDeleted] = 0");
    }
}
