using CarRental.Domain.Bookings;
using CarRental.Domain.Bookings.Quotes;
using CarRental.Domain.Common;
using CarRental.Domain.Referential;
using CarRental.Infrastructure.Persistence.Auditing;
using CarRental.Infrastructure.Persistence.Conventions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CarRental.Infrastructure.Persistence.Configurations.Bookings;

/// <summary>
/// The rental file (temporal table <c>bookings.Bookings</c>): readable number and channel reference unique within the
/// agency (ADR-0067), indexes of the occupancy reads (category, status, pick-up, return) and of the screens (agency and
/// dates, parties, vehicle, deadlines of the expiry job). Other modules are referenced by id only (agency foreign key,
/// ADR-0012).
/// </summary>
internal sealed class BookingConfiguration : IEntityTypeConfiguration<Booking>
{
    public const int NumberMaxLength = 24;

    public void Configure(EntityTypeBuilder<Booking> builder)
    {
        builder.ToTemporalTable("Bookings", Schemas.Bookings);
        builder.HasKey(b => b.Id);

        builder.Property(b => b.Number).HasMaxLength(NumberMaxLength).IsUnicode(false).IsRequired();
        builder.Property(b => b.ChannelReference).HasMaxLength(Booking.ChannelReferenceMaxLength);
        builder.Property(b => b.StatusReason).HasMaxLength(Booking.ReasonMaxLength);
        builder.Property(b => b.ExtraSelection).HasMaxLength(Booking.ExtrasMaxLength).IsUnicode(false).IsRequired();
        builder.Property(b => b.PromoCode).HasMaxLength(Booking.PromoCodeMaxLength);
        builder.Property(b => b.Notes).HasMaxLength(Booking.NotesMaxLength);

        // Technical counter bumped by every command, so that the rowversion guards the timeline too.
        builder.Property(b => b.LastEventSequence).IsAuditIgnored();

        builder.ComplexProperty(b => b.Attribution, attribution =>
        {
            attribution.Property(a => a.Source).HasColumnName("AttributionSource").HasMaxLength(BookingAttribution.FieldMaxLength);
            attribution.Property(a => a.UtmSource).HasColumnName("UtmSource").HasMaxLength(BookingAttribution.FieldMaxLength);
            attribution.Property(a => a.UtmMedium).HasColumnName("UtmMedium").HasMaxLength(BookingAttribution.FieldMaxLength);
            attribution.Property(a => a.UtmCampaign).HasColumnName("UtmCampaign").HasMaxLength(BookingAttribution.FieldMaxLength);
            attribution.Property(a => a.UtmTerm).HasColumnName("UtmTerm").HasMaxLength(BookingAttribution.FieldMaxLength);
            attribution.Property(a => a.UtmContent).HasColumnName("UtmContent").HasMaxLength(BookingAttribution.FieldMaxLength);
            attribution.Ignore(a => a.IsEmpty);
        });
        builder.ComplexProperty(b => b.ComplianceWaiver, waiver =>
        {
            waiver.Property(w => w.VehicleId).HasColumnName("WaiverVehicleId");
            waiver.Property(w => w.ByUserId).HasColumnName("WaiverByUserId");
            waiver.Property(w => w.ByName).HasColumnName("WaiverByName").HasMaxLength(AuditActor.UserNameMaxLength);
            waiver.Property(w => w.AtUtc).HasColumnName("WaiverAtUtc");
            waiver.Property(w => w.Reason).HasColumnName("WaiverReason").HasMaxLength(Booking.ReasonMaxLength);
        });

        builder.Ignore(b => b.Rental);
        builder.Ignore(b => b.Parties);
        builder.Ignore(b => b.Extras);
        builder.Ignore(b => b.CurrentDeadline);
        builder.Ignore(b => b.IsComplianceWaived);
        builder.Ignore(b => b.CurrentChanges);

        builder.HasOne<Agency>().WithMany().HasForeignKey(b => b.AgencyId).OnDelete(DeleteBehavior.Restrict);

        // The stored timeline is read by the queries; the aggregate only adds rows.
        builder.HasMany(b => b.Events).WithOne().HasForeignKey(e => e.BookingId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(b => b.Events).HasField("_events").UsePropertyAccessMode(PropertyAccessMode.Field);

        // One series of numbers per agency (ADR-0067): a number is unique within its agency, and so is the reference of
        // a booking in a channel (each agency has its own channel accounts).
        builder.HasIndex(b => new { b.AgencyId, b.Number }).IsUnique();
        builder.HasIndex(b => new { b.AgencyId, b.Channel, b.ChannelReference }).IsUnique().HasFilter("[ChannelReference] IS NOT NULL");
        builder.HasIndex(b => new { b.CategoryId, b.Status, b.PickupAt, b.ReturnAt });
        builder.HasIndex(b => new { b.AgencyId, b.PickupAt });
        builder.HasIndex(b => new { b.AgencyId, b.ReturnAt });
        builder.HasIndex(b => new { b.Status, b.HoldExpiresAt });
        builder.HasIndex(b => b.MainDriverId);
        builder.HasIndex(b => b.SecondaryDriverId);
        builder.HasIndex(b => b.PayerCustomerId);
        builder.HasIndex(b => b.AssignedVehicleId);
    }
}

/// <summary>Timeline rows of the files (added only, numbered per file): a journal itself, left out of the audit log.</summary>
internal sealed class BookingEventConfiguration : IEntityTypeConfiguration<BookingEvent>
{
    public void Configure(EntityTypeBuilder<BookingEvent> builder)
    {
        builder.ToTable("BookingEvents", Schemas.Bookings);
        builder.ExcludeFromAudit();
        builder.HasKey(e => e.Id);
        builder.Property(e => e.ActorName).HasMaxLength(BookingActor.NameMaxLength).IsRequired();
        builder.Property(e => e.Reason).HasMaxLength(Booking.ReasonMaxLength);
        builder.Property(e => e.Data).HasMaxLength(BookingEvent.DataMaxLength);
        builder.Ignore(e => e.Facts);
        builder.HasIndex(e => new { e.BookingId, e.Sequence }).IsUnique();
    }
}

/// <summary>
/// Frozen quotes (temporal table <c>bookings.Quotes</c>, never updated): unique public code, one version number per
/// file, the JSON snapshots kept out of the audit log (they are the quote itself, immutable).
/// </summary>
internal sealed class QuoteConfiguration : IEntityTypeConfiguration<Quote>
{
    public void Configure(EntityTypeBuilder<Quote> builder)
    {
        builder.ToTemporalTable("Quotes", Schemas.Bookings);
        builder.HasKey(q => q.Id);
        builder.Property(q => q.Code).HasMaxLength(QuoteCode.Length).IsFixedLength().IsUnicode(false).IsRequired();
        builder.Property(q => q.CreatedBy).HasMaxLength(AuditActor.UserNameMaxLength);
        builder.Property(q => q.PromoCode).HasMaxLength(Booking.PromoCodeMaxLength);
        builder.Property(q => q.ManualDiscountReason).HasMaxLength(Quote.ReasonMaxLength);
        builder.Property(q => q.AppliedPromotionsJson).IsRequired().IsAuditIgnored();
        builder.Property(q => q.InputsJson).IsRequired().IsAuditIgnored();
        builder.Property(q => q.DailyRatesJson).IsRequired().IsAuditIgnored();

        builder.HasOne<Agency>().WithMany().HasForeignKey(q => q.AgencyId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Booking>().WithMany().HasForeignKey(q => q.BookingId).OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(q => q.Lines).WithOne().HasForeignKey(l => l.QuoteId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(q => q.Lines).HasField("_lines").UsePropertyAccessMode(PropertyAccessMode.Field).AutoInclude();

        // Unique on the whole platform on purpose: the code is drawn at random by the platform (never typed by an agency)
        // and a public link carries it alone (devis=Q7F3K2); it is only ever read within the agency of the site.
        builder.HasIndex(q => q.Code).IsUnique();
        builder.HasIndex(q => new { q.BookingId, q.Version }).IsUnique().HasFilter("[BookingId] IS NOT NULL");
    }
}

/// <summary>Lines of the frozen quotes (never changed): left out of the audit log, the quote row carries the totals.</summary>
internal sealed class QuoteLineConfiguration : IEntityTypeConfiguration<QuoteLine>
{
    public void Configure(EntityTypeBuilder<QuoteLine> builder)
    {
        builder.ToTable("QuoteLines", Schemas.Bookings);
        builder.ExcludeFromAudit();
        builder.HasKey(l => l.Id);
        builder.Property(l => l.Code).HasMaxLength(QuoteContent.LineCodeMaxLength).IsUnicode(false).IsRequired();
        builder.Property(l => l.LabelKey).HasMaxLength(QuoteContent.LabelKeyMaxLength).IsUnicode(false).IsRequired();
        builder.Property(l => l.VatRate).HasPrecision(5, 4);

        // Invariant label arguments as a JSON array (numbers with a dot, names).
        builder.PrimitiveCollection(l => l.LabelArguments).IsRequired();
        builder.HasIndex(l => new { l.QuoteId, l.Position }).IsUnique();
    }
}
