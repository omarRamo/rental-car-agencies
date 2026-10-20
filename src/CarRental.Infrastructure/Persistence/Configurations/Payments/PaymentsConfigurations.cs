using CarRental.Domain.Cash;
using CarRental.Domain.Common;
using CarRental.Domain.Payments;
using CarRental.Domain.Referential;
using CarRental.Infrastructure.Persistence.Conventions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CarRental.Infrastructure.Persistence.Configurations.Payments;

/// <summary>
/// Payments and refunds (temporal: the state of every payment at any date). A refund references the incoming payment it
/// pays back; cash references its cash session. Bookings are referenced by id only (no foreign key across the modules
/// built in parallel), with an index for the lists of a booking.
/// </summary>
internal sealed class PaymentConfiguration : IEntityTypeConfiguration<Payment>
{
    public void Configure(EntityTypeBuilder<Payment> builder)
    {
        builder.ToTemporalTable("Payments", Schemas.Payments);
        builder.HasKey(p => p.Id);
        builder.Property(p => p.ExternalReference).HasMaxLength(Payment.ReferenceMaxLength);
        builder.Property(p => p.Notes).HasMaxLength(Payment.NotesMaxLength);
        builder.Property(p => p.StatusReason).HasMaxLength(Payment.ReasonMaxLength);
        builder.Property(p => p.RecordedByName).HasMaxLength(AuditActor.UserNameMaxLength);
        builder.Property(p => p.StatusChangedByName).HasMaxLength(AuditActor.UserNameMaxLength);

        builder.Ignore(p => p.IsOnline);
        builder.Ignore(p => p.IsSettled);
        builder.Ignore(p => p.RefundableAmount);

        builder.HasOne<Agency>().WithMany().HasForeignKey(p => p.AgencyId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Payment>().WithMany().HasForeignKey(p => p.OriginalPaymentId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<CashSession>().WithMany().HasForeignKey(p => p.CashSessionId).OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(p => p.BookingId);
        builder.HasIndex(p => new { p.AgencyId, p.RecordedAtUtc });

        // Daily totals by means of payment (reconciliations): settled payments only.
        builder.HasIndex(p => p.CapturedAtUtc).HasFilter("[CapturedAtUtc] IS NOT NULL");
    }
}

/// <summary>
/// Security deposits (temporal). A cash deposit references the cash sessions where it was received and settled. The
/// held deposits are indexed: they are the open liability to customers.
/// </summary>
internal sealed class DepositHoldConfiguration : IEntityTypeConfiguration<DepositHold>
{
    public void Configure(EntityTypeBuilder<DepositHold> builder)
    {
        builder.ToTemporalTable("DepositHolds", Schemas.Payments);
        builder.HasKey(d => d.Id);
        builder.Property(d => d.ExternalReference).HasMaxLength(DepositHold.ReferenceMaxLength);
        builder.Property(d => d.Notes).HasMaxLength(DepositHold.NotesMaxLength);
        builder.Property(d => d.RetentionJustification).HasMaxLength(DepositHold.JustificationMaxLength);
        builder.Property(d => d.AttachmentReference).HasMaxLength(DepositHold.AttachmentReferenceMaxLength);
        builder.Property(d => d.TakenByName).HasMaxLength(AuditActor.UserNameMaxLength);
        builder.Property(d => d.SettledByName).HasMaxLength(AuditActor.UserNameMaxLength);

        builder.Ignore(d => d.IsHeld);
        builder.Ignore(d => d.OpenLiability);
        builder.Ignore(d => d.ReturnedAmount);

        builder.HasOne<Agency>().WithMany().HasForeignKey(d => d.AgencyId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<CashSession>().WithMany().HasForeignKey(d => d.TakenInCashSessionId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<CashSession>().WithMany().HasForeignKey(d => d.SettledInCashSessionId).OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(d => d.BookingId);
        builder.HasIndex(d => new { d.AgencyId, d.TakenAtUtc });
        builder.HasIndex(d => new { d.AgencyId, d.Mode }).HasFilter("[Status] = 1").HasDatabaseName("IX_DepositHolds_Held");
    }
}
