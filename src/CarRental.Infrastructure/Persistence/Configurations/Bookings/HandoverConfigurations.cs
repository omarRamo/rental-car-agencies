using CarRental.Domain.Bookings;
using CarRental.Domain.Bookings.Handover;
using CarRental.Domain.Referential;
using CarRental.Infrastructure.Persistence.Auditing;
using CarRental.Infrastructure.Persistence.Conventions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CarRental.Infrastructure.Persistence.Configurations.Bookings;

/// <summary>
/// The inspections of the departures and returns (temporal table <c>bookings.Inspections</c>, ADR-0041): readings,
/// accessories, originals, the paper signature and the deferral in the columns of the row; photos, damages, signatures
/// and return charges in child tables loaded with it. One draft at most per file and kind (filtered unique index).
/// </summary>
internal sealed class InspectionConfiguration : IEntityTypeConfiguration<Inspection>
{
    public void Configure(EntityTypeBuilder<Inspection> builder)
    {
        builder.ToTemporalTable("Inspections", Schemas.Bookings);
        builder.HasKey(i => i.Id);

        builder.Property(i => i.OriginalsSelection).HasMaxLength(Inspection.OriginalsMaxLength).IsUnicode(false).IsRequired();
        builder.Property(i => i.Notes).HasMaxLength(Inspection.NotesMaxLength);
        builder.Property(i => i.StartedBy).HasMaxLength(BookingActor.NameMaxLength).IsRequired();
        builder.Property(i => i.CompletedBy).HasMaxLength(BookingActor.NameMaxLength);

        // Technical counter bumped by every change, so that the rowversion guards the children too.
        builder.Property(i => i.Revision).IsAuditIgnored();

        builder.ComplexProperty(i => i.Accessories, accessories =>
        {
            accessories.Property(a => a.SpareWheel).HasColumnName("AccessorySpareWheel");
            accessories.Property(a => a.Jack).HasColumnName("AccessoryJack");
            accessories.Property(a => a.WarningTriangle).HasColumnName("AccessoryWarningTriangle");
            accessories.Property(a => a.SafetyVest).HasColumnName("AccessorySafetyVest");
            accessories.Property(a => a.Keys).HasColumnName("AccessoryKeys");
            accessories.Property(a => a.ChildSeats).HasColumnName("AccessoryChildSeats");
            accessories.Property(a => a.Gps).HasColumnName("AccessoryGps");
            accessories.Property(a => a.VehiclePapers).HasColumnName("AccessoryVehiclePapers");
        });
        builder.ComplexProperty(i => i.PaperSignature, paper =>
        {
            paper.Property(p => p.Reason).HasColumnName("PaperSignatureReason").HasMaxLength(Inspection.ReasonMaxLength);
            paper.Property(p => p.ByUserId).HasColumnName("PaperSignatureByUserId");
            paper.Property(p => p.ByName).HasColumnName("PaperSignatureByName").HasMaxLength(BookingActor.NameMaxLength);
            paper.Property(p => p.AtUtc).HasColumnName("PaperSignatureAtUtc");
        });
        builder.ComplexProperty(i => i.Deferral, deferral =>
        {
            deferral.Property(d => d.BalanceDeferred).HasColumnName("DeferralBalance");
            deferral.Property(d => d.DepositWaived).HasColumnName("DeferralDepositWaived");
            deferral.Property(d => d.Reason).HasColumnName("DeferralReason").HasMaxLength(Inspection.ReasonMaxLength);
            deferral.Property(d => d.ByUserId).HasColumnName("DeferralByUserId");
            deferral.Property(d => d.ByName).HasColumnName("DeferralByName").HasMaxLength(BookingActor.NameMaxLength);
            deferral.Property(d => d.AtUtc).HasColumnName("DeferralAtUtc");
        });

        builder.Ignore(i => i.Originals);
        builder.Ignore(i => i.ReleasedFiles);
        builder.Ignore(i => i.IsDraft);
        builder.Ignore(i => i.MissingGuidedPhotos);
        builder.Ignore(i => i.IsSigned);
        builder.Ignore(i => i.ChargesTotal);
        builder.Ignore(i => i.NewDamageIds);

        builder.HasOne<Agency>().WithMany().HasForeignKey(i => i.AgencyId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Booking>().WithMany().HasForeignKey(i => i.BookingId).OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(i => i.Photos).WithOne().HasForeignKey(p => p.InspectionId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(i => i.Photos).HasField("_photos").UsePropertyAccessMode(PropertyAccessMode.Field).AutoInclude();
        builder.HasMany(i => i.Damages).WithOne().HasForeignKey(d => d.InspectionId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(i => i.Damages).HasField("_damages").UsePropertyAccessMode(PropertyAccessMode.Field).AutoInclude();
        builder.HasMany(i => i.Signatures).WithOne().HasForeignKey(s => s.InspectionId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(i => i.Signatures).HasField("_signatures").UsePropertyAccessMode(PropertyAccessMode.Field).AutoInclude();
        builder.HasMany(i => i.Charges).WithOne().HasForeignKey(c => c.InspectionId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(i => i.Charges).HasField("_charges").UsePropertyAccessMode(PropertyAccessMode.Field).AutoInclude();

        // One draft (InspectionStatus.Draft = 1) per file and kind: two tablets starting the same hand-over share it.
        builder.HasIndex(i => new { i.BookingId, i.Kind }).IsUnique().HasFilter("[Status] = 1");
        builder.HasIndex(i => new { i.BookingId, i.Kind, i.StartedAtUtc });
        builder.HasIndex(i => new { i.VehicleId, i.Status, i.CompletedAtUtc });
    }
}

/// <summary>Photos of the inspections (blob references in the private container <c>inspections</c>).</summary>
internal sealed class InspectionPhotoConfiguration : IEntityTypeConfiguration<InspectionPhoto>
{
    public void Configure(EntityTypeBuilder<InspectionPhoto> builder)
    {
        builder.ToTable("InspectionPhotos", Schemas.Bookings);
        builder.HasKey(p => p.Id);
        builder.Property(p => p.StoragePath).HasMaxLength(HandoverFile.PathMaxLength).IsUnicode(false).IsRequired();
        builder.Property(p => p.ContentType).HasMaxLength(HandoverFile.ContentTypeMaxLength).IsUnicode(false).IsRequired();
        builder.Property(p => p.ContentHash).HasMaxLength(HandoverFile.ContentHashLength).IsFixedLength().IsUnicode(false).IsRequired();
        builder.Property(p => p.ThumbnailPath).HasMaxLength(HandoverFile.PathMaxLength).IsUnicode(false);
        builder.Property(p => p.ThumbnailContentType).HasMaxLength(HandoverFile.ContentTypeMaxLength).IsUnicode(false);
        builder.Property(p => p.TakenBy).HasMaxLength(BookingActor.NameMaxLength).IsRequired();
        builder.HasIndex(p => p.InspectionId);
    }
}

/// <summary>Damages marked on the schematic (view, position in percent, type, severity, note, pre-existing or new).</summary>
internal sealed class DamageMarkConfiguration : IEntityTypeConfiguration<DamageMark>
{
    public void Configure(EntityTypeBuilder<DamageMark> builder)
    {
        builder.ToTable("DamageMarks", Schemas.Bookings);
        builder.HasKey(d => d.Id);
        builder.Property(d => d.X).HasPrecision(5, 2);
        builder.Property(d => d.Y).HasPrecision(5, 2);
        builder.Property(d => d.Note).HasMaxLength(DamageMark.NoteMaxLength);
        builder.Property(d => d.RecordedBy).HasMaxLength(BookingActor.NameMaxLength).IsRequired();
        builder.HasIndex(d => d.InspectionId);
    }
}

/// <summary>Signatures drawn on the tablet (PNG references in the private container <c>inspections</c>).</summary>
internal sealed class InspectionSignatureConfiguration : IEntityTypeConfiguration<InspectionSignature>
{
    public void Configure(EntityTypeBuilder<InspectionSignature> builder)
    {
        builder.ToTable("InspectionSignatures", Schemas.Bookings);
        builder.HasKey(s => s.Id);
        builder.Property(s => s.SignerName).HasMaxLength(InspectionSignature.SignerNameMaxLength).IsRequired();
        builder.Property(s => s.StoragePath).HasMaxLength(HandoverFile.PathMaxLength).IsUnicode(false).IsRequired();
        builder.Property(s => s.ContentHash).HasMaxLength(HandoverFile.ContentHashLength).IsFixedLength().IsUnicode(false).IsRequired();
        builder.Property(s => s.RecordedBy).HasMaxLength(BookingActor.NameMaxLength).IsRequired();
        builder.HasIndex(s => new { s.InspectionId, s.Role }).IsUnique();
    }
}

/// <summary>
/// Return charges (temporal table <c>bookings.ReturnCharges</c>, ADR-0042): a revision replaces the lines and the history
/// keeps the previous ones; amounts in millimes, the VAT rate with four decimals.
/// </summary>
internal sealed class ReturnChargeConfiguration : IEntityTypeConfiguration<ReturnCharge>
{
    public void Configure(EntityTypeBuilder<ReturnCharge> builder)
    {
        builder.ToTemporalTable("ReturnCharges", Schemas.Bookings);
        builder.HasKey(c => c.Id);
        builder.Property(c => c.VatRate).HasPrecision(5, 4);
        builder.Property(c => c.Description).HasMaxLength(ReturnCharge.DescriptionMaxLength);
        builder.Property(c => c.Reason).HasMaxLength(Inspection.ReasonMaxLength);
        builder.Property(c => c.RecordedBy).HasMaxLength(BookingActor.NameMaxLength).IsRequired();

        // Invariant label arguments as a JSON array (kilometres, eighths, days, amounts).
        builder.PrimitiveCollection(c => c.LabelArguments).IsRequired();
        builder.Ignore(c => c.IsWaived);
        builder.HasIndex(c => new { c.InspectionId, c.Position }).IsUnique();
    }
}

/// <summary>Rental contracts (temporal table <c>bookings.RentalContracts</c>): one version number per file, the PDF reference.</summary>
internal sealed class RentalContractConfiguration : IEntityTypeConfiguration<RentalContract>
{
    public void Configure(EntityTypeBuilder<RentalContract> builder)
    {
        builder.ToTemporalTable("RentalContracts", Schemas.Bookings);
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Number).HasMaxLength(RentalContract.NumberMaxLength).IsUnicode(false).IsRequired();
        builder.Property(c => c.StoragePath).HasMaxLength(HandoverFile.PathMaxLength).IsUnicode(false).IsRequired();
        builder.Property(c => c.ContentHash).HasMaxLength(HandoverFile.ContentHashLength).IsFixedLength().IsUnicode(false).IsRequired();
        builder.Property(c => c.GeneratedBy).HasMaxLength(BookingActor.NameMaxLength).IsRequired();
        builder.Property(c => c.SentTo).HasMaxLength(RentalContract.EmailMaxLength).IsAuditSensitive();
        builder.Ignore(c => c.FileName);

        builder.HasOne<Agency>().WithMany().HasForeignKey(c => c.AgencyId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Booking>().WithMany().HasForeignKey(c => c.BookingId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Inspection>().WithMany().HasForeignKey(c => c.InspectionId).OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(c => new { c.BookingId, c.Version }).IsUnique();
    }
}
