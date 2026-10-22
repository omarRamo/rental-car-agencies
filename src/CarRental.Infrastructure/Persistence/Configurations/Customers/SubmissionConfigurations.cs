using CarRental.Domain.Common;
using CarRental.Domain.Customers;
using CarRental.Domain.Referential;
using CarRental.Infrastructure.Persistence.Auditing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CarRental.Infrastructure.Persistence.Configurations.Customers;

/// <summary>
/// Public submissions of drivers (ADR-0050): what visitors typed for a booking, kept apart from the persons. Not a
/// temporal table: an anonymized person's submissions are deleted for good. Personal data is masked in the audit log.
/// </summary>
internal sealed class DriverSubmissionConfiguration : IEntityTypeConfiguration<DriverSubmission>
{
    public void Configure(EntityTypeBuilder<DriverSubmission> builder)
    {
        builder.ToTable("DriverSubmissions", Schemas.Customers);
        builder.HasKey(s => s.Id);
        builder.Property(s => s.FirstName).HasMaxLength(Person.NameMaxLength).IsAuditSensitive();
        builder.Property(s => s.LastName).HasMaxLength(Person.NameMaxLength).IsAuditSensitive();
        builder.Property(s => s.Nationality).HasMaxLength(CountryCodes.Length).IsFixedLength().IsUnicode(false);
        builder.Property(s => s.BirthDate).IsAuditSensitive();
        builder.Property(s => s.Phone).HasMaxLength(PhoneNumbers.MaxLength).IsUnicode(false).IsAuditSensitive();
        builder.Property(s => s.Email).HasMaxLength(EmailAddresses.MaxLength).IsAuditSensitive();
        builder.Property(s => s.ReviewedByName).HasMaxLength(AuditActor.UserNameMaxLength);
        builder.Property(s => s.LastModifiedAtUtc).IsAuditIgnored();
        builder.Ignore(s => s.IsPending);
        builder.Ignore(s => s.Details);

        builder.HasOne<Agency>().WithMany().HasForeignKey(s => s.AgencyId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Person>().WithMany().HasForeignKey(s => s.PersonId).OnDelete(DeleteBehavior.Restrict);
        builder.HasMany(s => s.Documents).WithOne().HasForeignKey(d => d.SubmissionId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(s => s.Documents).HasField("_documents").UsePropertyAccessMode(PropertyAccessMode.Field).AutoInclude();

        builder.HasIndex(s => new { s.PersonId, s.State });
        builder.HasIndex(s => new { s.BookingId, s.State });
    }
}

/// <summary>Documents of the public submissions; numbers masked in the audit log.</summary>
internal sealed class SubmittedDocumentConfiguration : IEntityTypeConfiguration<SubmittedDocument>
{
    public void Configure(EntityTypeBuilder<SubmittedDocument> builder)
    {
        builder.ToTable("SubmittedDocuments", Schemas.Customers);
        builder.HasKey(d => d.Id);
        builder.Property(d => d.Number).HasMaxLength(DocumentNumbers.NumberMaxLength).IsAuditSensitive();
        builder.Property(d => d.NumberKey).HasMaxLength(DocumentNumbers.KeyMaxLength).IsUnicode(false).IsAuditSensitive();
        builder.Property(d => d.IssuingCountry).HasMaxLength(CountryCodes.Length).IsFixedLength().IsUnicode(false);
        builder.ComplexProperty(d => d.FrontImage, image => ImageColumns(image, "FrontImage"));
        builder.ComplexProperty(d => d.BackImage, image => ImageColumns(image, "BackImage"));
        builder.Ignore(d => d.HasDetails);
        builder.Ignore(d => d.Details);
    }

    internal static void ImageColumns(ComplexPropertyBuilder<DocumentImage> image, string prefix)
    {
        image.Property(i => i.Path).HasColumnName($"{prefix}Path").HasMaxLength(DocumentImage.PathMaxLength).IsUnicode(false);
        image.Property(i => i.ContentType).HasColumnName($"{prefix}ContentType").HasMaxLength(DocumentImage.ContentTypeMaxLength).IsUnicode(false);
        image.Property(i => i.Length).HasColumnName($"{prefix}Length");
        image.Property(i => i.UploadedAtUtc).HasColumnName($"{prefix}UploadedAtUtc");
    }
}

/// <summary>
/// Images uploaded by visitors before their submission: technical staging, left out of the audit log, purged across
/// agencies by age (no index by agency, no foreign key: a staging row lives a day at most).
/// </summary>
internal sealed class StagedDocumentImageConfiguration : IEntityTypeConfiguration<StagedDocumentImage>
{
    public void Configure(EntityTypeBuilder<StagedDocumentImage> builder)
    {
        builder.ToTable("StagedDocumentImages", Schemas.Customers);
        builder.ExcludeFromAudit();
        builder.HasKey(s => s.Id);
        builder.ComplexProperty(s => s.Image, image =>
        {
            image.Property(i => i.Path).HasColumnName("Path").HasMaxLength(DocumentImage.PathMaxLength).IsUnicode(false);
            image.Property(i => i.ContentType).HasColumnName("ContentType").HasMaxLength(DocumentImage.ContentTypeMaxLength).IsUnicode(false);
            image.Property(i => i.Length).HasColumnName("Length");
            image.Property(i => i.UploadedAtUtc).HasColumnName("UploadedAtUtc");
        });
        builder.Ignore(s => s.IsConsumed);
    }
}
