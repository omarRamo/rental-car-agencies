using CarRental.Domain.Bookings;
using CarRental.Domain.Bookings.Access;
using CarRental.Domain.Bookings.Quotes;
using CarRental.Domain.Referential;
using CarRental.Infrastructure.Persistence.Auditing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CarRental.Infrastructure.Persistence.Configurations.Bookings;

/// <summary>
/// Funnel sessions of the public site (ADR-0049): the hash of the token is the key of the addresses (unique), the
/// drivers as typed are personal data kept a few days at most (purge job). Left out of the audit log: a session is a
/// visitor's draft, not a business command (the request it leads to is audited).
/// </summary>
internal sealed class FunnelSessionConfiguration : IEntityTypeConfiguration<FunnelSession>
{
    public void Configure(EntityTypeBuilder<FunnelSession> builder)
    {
        builder.ToTable("FunnelSessions", Schemas.Bookings);
        builder.ExcludeFromAudit();
        builder.HasKey(s => s.Id);
        builder.Property(s => s.TokenHash).HasMaxLength(AccessSecrets.HashLength).IsFixedLength().IsRequired();
        builder.Property(s => s.Culture).HasMaxLength(FunnelSession.CultureMaxLength).IsUnicode(false).IsRequired();
        builder.Property(s => s.CategoryCode).HasMaxLength(FunnelSession.CategoryCodeLength).IsUnicode(false).IsRequired();
        builder.Property(s => s.SearchQuery).HasMaxLength(FunnelSession.SearchQueryMaxLength).IsRequired();
        builder.Property(s => s.QuoteCode).HasMaxLength(QuoteCode.Length).IsFixedLength().IsUnicode(false).IsRequired();
        builder.Property(s => s.ExtraSelection).HasMaxLength(Booking.ExtrasMaxLength).IsUnicode(false).IsRequired();
        builder.Property(s => s.DriversJson).HasMaxLength(FunnelSession.DriversMaxLength);
        builder.Property(s => s.DraftJson).HasMaxLength(FunnelSession.DriversMaxLength);
        builder.Property(s => s.PendingNotice).HasMaxLength(FunnelSession.NoticeMaxLength).IsUnicode(false);
        builder.Property(s => s.BookingNumber).HasMaxLength(BookingConfiguration.NumberMaxLength).IsUnicode(false);
        builder.ComplexProperty(s => s.Attribution, attribution =>
        {
            attribution.Property(a => a.Source).HasColumnName("AttributionSource").HasMaxLength(BookingAttribution.FieldMaxLength);
            attribution.Property(a => a.UtmSource).HasColumnName("UtmSource").HasMaxLength(BookingAttribution.FieldMaxLength);
            attribution.Property(a => a.UtmMedium).HasColumnName("UtmMedium").HasMaxLength(BookingAttribution.FieldMaxLength);
            attribution.Property(a => a.UtmCampaign).HasColumnName("UtmCampaign").HasMaxLength(BookingAttribution.FieldMaxLength);
            attribution.Property(a => a.UtmTerm).HasColumnName("UtmTerm").HasMaxLength(BookingAttribution.FieldMaxLength);
            attribution.Property(a => a.UtmContent).HasColumnName("UtmContent").HasMaxLength(BookingAttribution.FieldMaxLength);
            attribution.Ignore(a => a.IsEmpty);
        });
        builder.Ignore(s => s.Extras);
        builder.Ignore(s => s.IsSubmitted);
        builder.Ignore(s => s.HasDrivers);

        builder.HasOne<Agency>().WithMany().HasForeignKey(s => s.AgencyId).OnDelete(DeleteBehavior.Restrict);

        // Unique on the whole platform on purpose: the hash of a random secret token, the only key of the addresses.
        builder.HasIndex(s => s.TokenHash).IsUnique();
        builder.HasIndex(s => s.ExpiresAtUtc);
    }
}

/// <summary>Photos uploaded during a funnel session (insert only), by session.</summary>
internal sealed class FunnelUploadConfiguration : IEntityTypeConfiguration<FunnelUpload>
{
    public void Configure(EntityTypeBuilder<FunnelUpload> builder)
    {
        builder.ToTable("FunnelUploads", Schemas.Bookings);
        builder.ExcludeFromAudit();
        builder.HasKey(u => u.Id);
        builder.Property(u => u.Slot).HasMaxLength(UploadSlot.KeyMaxLength).IsUnicode(false).IsRequired();
        builder.Property(u => u.ContentType).HasMaxLength(FunnelUpload.ContentTypeMaxLength).IsUnicode(false).IsRequired();
        builder.HasOne<FunnelSession>().WithMany().HasForeignKey(u => u.SessionId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(u => new { u.SessionId, u.UploadedAtUtc });
    }
}

/// <summary>
/// Access grants of "Ma réservation" (ADR-0051): hashes of the link and of the code, instants, attempts. A journal of the
/// accesses itself, left out of the audit log; purged after a month.
/// </summary>
internal sealed class BookingAccessGrantConfiguration : IEntityTypeConfiguration<BookingAccessGrant>
{
    public void Configure(EntityTypeBuilder<BookingAccessGrant> builder)
    {
        builder.ToTable("BookingAccessGrants", Schemas.Bookings);
        builder.ExcludeFromAudit();
        builder.HasKey(g => g.Id);
        builder.Property(g => g.TokenHash).HasMaxLength(AccessSecrets.HashLength).IsFixedLength().IsRequired();
        builder.Property(g => g.CodeHash).HasMaxLength(AccessSecrets.HashLength).IsFixedLength();
        builder.Property(g => g.CodeSalt).HasMaxLength(AccessSecrets.SaltLength).IsFixedLength();
        builder.Property(g => g.Culture).HasMaxLength(BookingAccessGrant.CultureMaxLength).IsUnicode(false).IsRequired();
        builder.Ignore(g => g.IsUsed);
        builder.HasOne<Agency>().WithMany().HasForeignKey(g => g.AgencyId).OnDelete(DeleteBehavior.Restrict);

        // Unique on the whole platform on purpose: the hash of a random secret token of a link.
        builder.HasIndex(g => g.TokenHash).IsUnique();
        builder.HasIndex(g => new { g.BookingId, g.CreatedAtUtc });
        builder.HasIndex(g => g.CreatedAtUtc);
    }
}

/// <summary>The language and preferences of the customer of a booking made online (no personal data), one per booking.</summary>
internal sealed class BookingCustomerProfileConfiguration : IEntityTypeConfiguration<BookingCustomerProfile>
{
    public void Configure(EntityTypeBuilder<BookingCustomerProfile> builder)
    {
        builder.ToTable("BookingCustomerProfiles", Schemas.Bookings);
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Culture).HasMaxLength(BookingAccessGrant.CultureMaxLength).IsUnicode(false).IsRequired();
        builder.Property(p => p.PublicBaseUrl).HasMaxLength(BookingCustomerProfile.BaseUrlMaxLength).IsUnicode(false);
        builder.HasIndex(p => p.BookingId).IsUnique();
    }
}

/// <summary>The payment state of a booking kept from the confirmed changes of its online payments, one per booking.</summary>
internal sealed class BookingPaymentTrackConfiguration : IEntityTypeConfiguration<BookingPaymentTrack>
{
    public void Configure(EntityTypeBuilder<BookingPaymentTrack> builder)
    {
        builder.ToTable("BookingPaymentTracks", Schemas.Bookings);
        builder.HasKey(t => t.Id);
        builder.HasOne<Booking>().WithMany().HasForeignKey(t => t.BookingId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(t => t.BookingId).IsUnique();
    }
}
