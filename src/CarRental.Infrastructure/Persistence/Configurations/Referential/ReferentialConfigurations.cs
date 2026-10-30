using CarRental.Domain.Referential;
using CarRental.Infrastructure.Persistence.Conventions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CarRental.Infrastructure.Persistence.Configurations.Referential;

internal sealed class AgencyConfiguration : IEntityTypeConfiguration<Agency>
{
    public void Configure(EntityTypeBuilder<Agency> builder)
    {
        builder.ToTemporalTable("Agencies", Schemas.Referential);
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Name).HasMaxLength(Agency.NameMaxLength).IsRequired();
        builder.Property(a => a.LegalName).HasMaxLength(Agency.LegalNameMaxLength);
        builder.Property(a => a.TaxIdentifier).HasMaxLength(Agency.TaxIdentifierMaxLength);
        builder.Property(a => a.TradeRegisterNumber).HasMaxLength(Agency.TradeRegisterNumberMaxLength);
        builder.Property(a => a.LegalAddress).HasMaxLength(Agency.LegalAddressMaxLength);
        builder.Property(a => a.InvoiceFooter).HasMaxLength(Agency.InvoiceFooterMaxLength);
        builder.Property(a => a.City).HasMaxLength(Agency.CityMaxLength).IsRequired();
        builder.Property(a => a.Address).HasMaxLength(Agency.AddressMaxLength);
        builder.Property(a => a.Phone).HasMaxLength(Agency.PhoneMaxLength);
        builder.Property(a => a.Email).HasMaxLength(Agency.EmailMaxLength);
        builder.Property(a => a.Latitude).HasPrecision(9, Agency.CoordinateDecimals);
        builder.Property(a => a.Longitude).HasPrecision(9, Agency.CoordinateDecimals);
        builder.Property(a => a.Slug).HasMaxLength(AgencySlug.MaxLength).IsUnicode(false).IsRequired();
        builder.Property(a => a.Modules).HasConversion<int>();

        // The address of the public site is unique on the whole platform, deleted agencies included (their links must
        // never open another agency's site).
        builder.HasIndex(a => a.Slug).IsUnique();
    }
}

internal sealed class AgencyDomainConfiguration : IEntityTypeConfiguration<AgencyDomain>
{
    public void Configure(EntityTypeBuilder<AgencyDomain> builder)
    {
        builder.ToTable("AgencyDomains", Schemas.Referential);
        builder.HasKey(d => d.Id);
        builder.Property(d => d.HostName).HasMaxLength(AgencyHostName.MaxLength).IsUnicode(false).IsRequired();
        builder.HasOne<Agency>().WithMany().HasForeignKey(d => d.AgencyId).OnDelete(DeleteBehavior.Cascade);

        // A host name serves one site on the whole platform (a route key, like Agency.Slug, ADR-0069).
        builder.HasIndex(d => d.HostName).IsUnique();
        builder.HasIndex(d => new { d.AgencyId, d.IsPrimary });
    }
}

internal sealed class LocationConfiguration : IEntityTypeConfiguration<Location>
{
    public void Configure(EntityTypeBuilder<Location> builder)
    {
        builder.ToTemporalTable("Locations", Schemas.Referential);
        builder.HasKey(l => l.Id);
        builder.Property(l => l.Code).HasMaxLength(Location.CodeMaxLength).IsUnicode(false).IsRequired();
        builder.Property(l => l.Name).HasMaxLength(Location.NameMaxLength).IsRequired();
        builder.Property(l => l.Address).HasMaxLength(Location.AddressMaxLength);
        builder.HasOne<Agency>().WithMany().HasForeignKey(l => l.AgencyId).OnDelete(DeleteBehavior.Restrict);

        // A code is unique among the live locations of an agency (soft-deleted rows free their code).
        builder.HasIndex(l => new { l.AgencyId, l.Code }).IsUnique().HasFilter("[IsDeleted] = 0");
    }
}

internal sealed class SettingConfiguration : IEntityTypeConfiguration<Setting>
{
    public void Configure(EntityTypeBuilder<Setting> builder)
    {
        builder.ToTemporalTable("Settings", Schemas.Referential);
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Key).HasMaxLength(Setting.KeyMaxLength).IsUnicode(false).IsRequired();
        builder.Property(s => s.Value).HasMaxLength(Setting.ValueMaxLength).IsRequired();
        builder.HasOne<Agency>().WithMany().HasForeignKey(s => s.AgencyId).OnDelete(DeleteBehavior.Restrict);

        // One platform value (AgencyId NULL) and one value per agency for each key: no "IS NOT NULL" filter, so that
        // SQL Server treats the NULL agency as a single value.
        builder.HasIndex(s => new { s.Key, s.AgencyId }).IsUnique().HasFilter(null);
    }
}
