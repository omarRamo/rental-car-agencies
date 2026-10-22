using CarRental.Domain.Customers;
using CarRental.Domain.Referential;
using CarRental.Infrastructure.Persistence.Auditing;
using CarRental.Infrastructure.Persistence.Conventions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CarRental.Infrastructure.Persistence.Configurations.Customers;

/// <summary>
/// Person aggregate: persons (temporal), identity documents (temporal), consents and blacklist entries. Personal data
/// and document numbers are masked in the audit log; the normalized keys (phone, e-mail, search name, document number
/// key) are indexed for the search and the deduplication (ADR-0027), within the agency of the person (ADR-0067).
/// </summary>
internal sealed class PersonConfiguration : IEntityTypeConfiguration<Person>
{
    public void Configure(EntityTypeBuilder<Person> builder)
    {
        builder.ToTemporalTable("Persons", Schemas.Customers);
        builder.HasKey(p => p.Id);
        builder.HasOne<Agency>().WithMany().HasForeignKey(p => p.AgencyId).OnDelete(DeleteBehavior.Restrict);
        builder.Property(p => p.FirstName).HasMaxLength(Person.NameMaxLength).IsRequired().IsAuditSensitive();
        builder.Property(p => p.LastName).HasMaxLength(Person.NameMaxLength).IsRequired().IsAuditSensitive();
        builder.Property(p => p.SearchName).HasMaxLength(Person.SearchNameMaxLength).IsRequired().IsAuditSensitive();
        builder.Property(p => p.Nationality).HasMaxLength(CountryCodes.Length).IsFixedLength().IsUnicode(false);
        builder.Property(p => p.BirthDate).IsAuditSensitive();
        builder.Property(p => p.Phone).HasMaxLength(PhoneNumbers.MaxLength).IsUnicode(false).IsAuditSensitive();
        builder.Property(p => p.Email).HasMaxLength(EmailAddresses.MaxLength).IsAuditSensitive();
        builder.Property(p => p.LastModifiedAtUtc).IsAuditIgnored();
        builder.ComplexProperty(p => p.Address, address => AddressColumns.Configure(address, "Address"));

        builder.Ignore(p => p.IsAnonymized);
        builder.Ignore(p => p.IsBlacklisted);
        builder.Ignore(p => p.ActiveBlacklistEntry);
        builder.Ignore(p => p.FullName);
        builder.Ignore(p => p.CurrentDocuments);

        // The aggregate is always loaded whole: its invariants (one current document per type, one active consent per
        // purpose, one active blacklist entry) need its children.
        builder.HasMany(p => p.Documents).WithOne().HasForeignKey(d => d.PersonId).OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(p => p.Consents).WithOne().HasForeignKey(c => c.PersonId).OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(p => p.BlacklistEntries).WithOne().HasForeignKey(e => e.PersonId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(p => p.Documents).HasField("_documents").UsePropertyAccessMode(PropertyAccessMode.Field).AutoInclude();
        builder.Navigation(p => p.Consents).HasField("_consents").UsePropertyAccessMode(PropertyAccessMode.Field).AutoInclude();
        builder.Navigation(p => p.BlacklistEntries).HasField("_blacklistEntries").UsePropertyAccessMode(PropertyAccessMode.Field).AutoInclude();

        // Every read of an agency's staff is limited to its agency (query filter): the keys of the search lead with it.
        builder.HasIndex(p => new { p.AgencyId, p.Phone });
        builder.HasIndex(p => new { p.AgencyId, p.Email });
        builder.HasIndex(p => new { p.AgencyId, p.SearchName });
        builder.HasIndex(p => new { p.AgencyId, p.LastName, p.FirstName });
    }
}

internal sealed class IdentityDocumentConfiguration : IEntityTypeConfiguration<IdentityDocument>
{
    public void Configure(EntityTypeBuilder<IdentityDocument> builder)
    {
        builder.ToTemporalTable("IdentityDocuments", Schemas.Customers);
        builder.HasKey(d => d.Id);
        builder.Property(d => d.Number).HasMaxLength(DocumentNumbers.NumberMaxLength).IsRequired().IsAuditSensitive();
        builder.Property(d => d.NumberKey).HasMaxLength(DocumentNumbers.KeyMaxLength).IsUnicode(false).IsRequired().IsAuditSensitive();
        builder.Property(d => d.IssuingCountry).HasMaxLength(CountryCodes.Length).IsFixedLength().IsUnicode(false).IsRequired();
        builder.Property(d => d.VerifiedByName).HasMaxLength(IdentityDocument.VerifierNameMaxLength);
        builder.ComplexProperty(d => d.FrontImage, image => ImageColumns(image, "FrontImage"));
        builder.ComplexProperty(d => d.BackImage, image => ImageColumns(image, "BackImage"));

        builder.Ignore(d => d.IsCurrent);
        builder.Ignore(d => d.IsVerified);

        // One current document per number in an agency: the deduplication key of a person, also guaranteed by the
        // database. The same document may be on file in two agencies, each for its own customer (ADR-0067).
        builder.HasIndex(d => new { d.AgencyId, d.Type, d.IssuingCountry, d.NumberKey })
            .IsUnique()
            .HasFilter("[SupersededAtUtc] IS NULL");
        builder.HasIndex(d => new { d.AgencyId, d.NumberKey });
    }

    private static void ImageColumns(ComplexPropertyBuilder<DocumentImage> image, string prefix)
    {
        image.Property(i => i.Path).HasColumnName($"{prefix}Path").HasMaxLength(DocumentImage.PathMaxLength).IsUnicode(false);
        image.Property(i => i.ContentType).HasColumnName($"{prefix}ContentType").HasMaxLength(DocumentImage.ContentTypeMaxLength).IsUnicode(false);
        image.Property(i => i.Length).HasColumnName($"{prefix}Length");
        image.Property(i => i.UploadedAtUtc).HasColumnName($"{prefix}UploadedAtUtc");
    }
}

internal sealed class ConsentConfiguration : IEntityTypeConfiguration<Consent>
{
    public void Configure(EntityTypeBuilder<Consent> builder)
    {
        builder.ToTable("Consents", Schemas.Customers);
        builder.HasKey(c => c.Id);
        builder.Ignore(c => c.IsActive);
        builder.HasIndex(c => new { c.PersonId, c.Purpose });
    }
}

internal sealed class BlacklistEntryConfiguration : IEntityTypeConfiguration<BlacklistEntry>
{
    public void Configure(EntityTypeBuilder<BlacklistEntry> builder)
    {
        builder.ToTable("BlacklistEntries", Schemas.Customers);
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Reason).HasMaxLength(BlacklistEntry.ReasonMaxLength).IsRequired().IsAuditSensitive();
        builder.Property(e => e.LiftReason).HasMaxLength(BlacklistEntry.ReasonMaxLength).IsAuditSensitive();
        builder.Property(e => e.AddedByName).HasMaxLength(BlacklistEntry.AuthorNameMaxLength);
        builder.Property(e => e.LiftedByName).HasMaxLength(BlacklistEntry.AuthorNameMaxLength);
        builder.Ignore(e => e.IsActive);
        builder.HasIndex(e => e.PersonId);
    }
}

/// <summary>Payers (temporal): one individual customer per person, one company per tax identifier and agency.</summary>
internal sealed class CustomerConfiguration : IEntityTypeConfiguration<Customer>
{
    public void Configure(EntityTypeBuilder<Customer> builder)
    {
        builder.ToTemporalTable("Customers", Schemas.Customers);
        builder.HasKey(c => c.Id);
        builder.HasOne<Agency>().WithMany().HasForeignKey(c => c.AgencyId).OnDelete(DeleteBehavior.Restrict);
        builder.Property(c => c.LegalName).HasMaxLength(Customer.LegalNameMaxLength);
        builder.Property(c => c.SearchName).HasMaxLength(Customer.LegalNameMaxLength);
        builder.Property(c => c.TaxIdentifier).HasMaxLength(TaxIdentifiers.MaxLength).IsUnicode(false);
        builder.Property(c => c.TaxIdentifierKey).HasMaxLength(TaxIdentifiers.MaxLength).IsUnicode(false);
        builder.Property(c => c.ContactName).HasMaxLength(Customer.ContactNameMaxLength).IsAuditSensitive();
        builder.Property(c => c.Email).HasMaxLength(EmailAddresses.MaxLength).IsAuditSensitive();
        builder.Property(c => c.Phone).HasMaxLength(PhoneNumbers.MaxLength).IsUnicode(false).IsAuditSensitive();
        builder.Property(c => c.LastModifiedAtUtc).IsAuditIgnored();
        builder.ComplexProperty(c => c.BillingAddress, address => AddressColumns.Configure(address, "BillingAddress", sensitive: false));
        builder.HasOne<Person>().WithMany().HasForeignKey(c => c.PersonId).OnDelete(DeleteBehavior.Restrict);

        // Per agency (ADR-0067): a company is a payer of each agency it rents from, with the same tax identifier.
        builder.HasIndex(c => new { c.AgencyId, c.PersonId }).IsUnique().HasFilter("[PersonId] IS NOT NULL");
        builder.HasIndex(c => new { c.AgencyId, c.TaxIdentifierKey }).IsUnique().HasFilter("[Kind] = 2 AND [TaxIdentifierKey] IS NOT NULL");
        builder.HasIndex(c => new { c.AgencyId, c.SearchName });
    }
}

/// <summary>Columns of a <see cref="PostalAddress"/> stored in its owner's table (<c>{prefix}Line1</c>, <c>{prefix}City</c>…).</summary>
internal static class AddressColumns
{
    public static void Configure(ComplexPropertyBuilder<PostalAddress> address, string prefix, bool sensitive = true)
    {
        address.Property(a => a.Line1).HasColumnName($"{prefix}Line1").HasMaxLength(PostalAddress.LineMaxLength).HasAnnotation(AuditAnnotations.Sensitive, sensitive);
        address.Property(a => a.Line2).HasColumnName($"{prefix}Line2").HasMaxLength(PostalAddress.LineMaxLength).HasAnnotation(AuditAnnotations.Sensitive, sensitive);
        address.Property(a => a.PostalCode).HasColumnName($"{prefix}PostalCode").HasMaxLength(PostalAddress.PostalCodeMaxLength).HasAnnotation(AuditAnnotations.Sensitive, sensitive);
        address.Property(a => a.City).HasColumnName($"{prefix}City").HasMaxLength(PostalAddress.CityMaxLength).HasAnnotation(AuditAnnotations.Sensitive, sensitive);
        address.Property(a => a.Country).HasColumnName($"{prefix}Country").HasMaxLength(CountryCodes.Length).IsFixedLength().IsUnicode(false);
    }
}
