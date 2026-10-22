using CarRental.Domain.Cash;
using CarRental.Domain.Common;
using CarRental.Domain.Payments;
using CarRental.Domain.Referential;
using CarRental.Infrastructure.Persistence.Auditing;
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
        builder.Ignore(p => p.OrderNumber);
        builder.Ignore(p => p.IsAwaitingApproval);

        // Clic to Pay (ADR-0038, ADR-0039): the gateway side of an online payment; empty for counter payments.
        builder.Property(p => p.GatewayFormUrl).HasMaxLength(Payment.GatewayFormUrlMaxLength);
        builder.Property(p => p.GatewayReturnPath).HasMaxLength(Payment.LocalPathMaxLength);
        builder.Property(p => p.GatewayFailPath).HasMaxLength(Payment.LocalPathMaxLength);
        builder.Property(p => p.GatewayAttentionDetail).HasMaxLength(Payment.GatewayAttentionDetailMaxLength);
        builder.Property(p => p.ApprovedByName).HasMaxLength(AuditActor.UserNameMaxLength);

        builder.HasOne<Agency>().WithMany().HasForeignKey(p => p.AgencyId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Payment>().WithMany().HasForeignKey(p => p.OriginalPaymentId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<CashSession>().WithMany().HasForeignKey(p => p.CashSessionId).OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(p => p.BookingId);
        builder.HasIndex(p => new { p.AgencyId, p.RecordedAtUtc });

        // Daily totals by means of payment of an agency (reconciliations, journals): settled payments only.
        builder.HasIndex(p => new { p.AgencyId, p.CapturedAtUtc }).HasFilter("[CapturedAtUtc] IS NOT NULL");

        // Online payments: return from the payment page by gateway order id; catch-up and lists by status and date. Both
        // stay global (ADR-0067): the gateway order ids come from the single merchant account of the platform, and the
        // return of the browser and the catch-up read the orders across agencies.
        var clicToPay = (int)PaymentMethod.ClicToPay;
        builder.HasIndex(p => p.ExternalReference)
            .HasFilter($"[Method] = {clicToPay} AND [ExternalReference] IS NOT NULL")
            .HasDatabaseName("IX_Payments_GatewayOrder");
        builder.HasIndex(p => new { p.Status, p.RecordedAtUtc })
            .HasFilter($"[Method] = {clicToPay}")
            .HasDatabaseName("IX_Payments_Online");
    }
}

/// <summary>
/// The call log of the payment gateway (append only, never audited row by row: it is itself a log). No credentials, no
/// card data, no raw answer: only structured fields. Read by payment for the history of the back office. A record of the
/// agency of its payment, or of the platform (no agency) when no stored payment matches its order: no foreign key, like
/// the audit journals.
/// </summary>
internal sealed class GatewayCallLogConfiguration : IEntityTypeConfiguration<GatewayCallLog>
{
    public void Configure(EntityTypeBuilder<GatewayCallLog> builder)
    {
        builder.ToTable("GatewayCallLogs", Schemas.Payments);
        builder.ExcludeFromAudit();
        builder.HasKey(c => c.Id);
        builder.Property(c => c.OrderNumber).HasMaxLength(GatewayCallLog.OrderNumberMaxLength).IsUnicode(false).IsRequired();
        builder.Property(c => c.GatewayOrderId).HasMaxLength(GatewayCallLog.GatewayOrderIdMaxLength).IsUnicode(false);
        builder.Property(c => c.GatewayErrorCode).HasMaxLength(GatewayCallLog.ErrorCodeMaxLength).IsUnicode(false);
        builder.Property(c => c.GatewayErrorMessage).HasMaxLength(GatewayCallLog.ErrorMessageMaxLength);
        builder.Property(c => c.ActionCode).HasMaxLength(GatewayCallLog.ActionCodeMaxLength).IsUnicode(false);
        builder.Property(c => c.CorrelationId).HasMaxLength(GatewayCallLog.CorrelationIdMaxLength).IsUnicode(false);

        builder.HasIndex(c => new { c.PaymentId, c.StartedAtUtc });
        builder.HasIndex(c => c.StartedAtUtc);
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
