using CarRental.Domain.Cash;
using CarRental.Domain.Cash.Expenses;
using CarRental.Domain.Common;
using CarRental.Domain.Payments;
using CarRental.Domain.Referential;
using CarRental.Infrastructure.Persistence.Auditing;
using CarRental.Infrastructure.Persistence.Conventions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CarRental.Infrastructure.Persistence.Configurations.Cash;

/// <summary>
/// Cash sessions (temporal) and their movements, always loaded with the session. Two unique indexes guarantee the rules
/// of <see cref="CashSessionRules"/> under concurrency: one session per cashier, agency and business day, and one open
/// session per cashier.
/// </summary>
internal sealed class CashSessionConfiguration : IEntityTypeConfiguration<CashSession>
{
    public void Configure(EntityTypeBuilder<CashSession> builder)
    {
        builder.ToTemporalTable("CashSessions", Schemas.Cash);
        builder.HasKey(s => s.Id);
        builder.Property(s => s.CashierName).HasMaxLength(AuditActor.UserNameMaxLength).IsRequired();
        builder.Property(s => s.VarianceJustification).HasMaxLength(CashSession.JustificationMaxLength);
        builder.Property(s => s.ClosedByName).HasMaxLength(AuditActor.UserNameMaxLength);
        builder.Property(s => s.LockedByName).HasMaxLength(AuditActor.UserNameMaxLength);

        // Technical counter bumped by every movement; the movement itself is audited.
        builder.Property(s => s.LastSequence).IsAuditIgnored();

        builder.Ignore(s => s.IsOpen);
        builder.Ignore(s => s.HasVariance);

        builder.HasOne<Agency>().WithMany().HasForeignKey(s => s.AgencyId).OnDelete(DeleteBehavior.Restrict);
        builder.HasMany(s => s.Movements).WithOne().HasForeignKey(m => m.SessionId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(s => s.Movements).HasField("_movements").UsePropertyAccessMode(PropertyAccessMode.Field).AutoInclude();

        builder.HasIndex(s => new { s.CashierId, s.AgencyId, s.BusinessDate })
            .IsUnique()
            .HasDatabaseName("IX_CashSessions_Cashier_Agency_BusinessDate");

        // Global on purpose (ADR-0067): a person holds one drawer at a time, whatever the agency (the support included).
        builder.HasIndex(s => s.CashierId)
            .IsUnique()
            .HasFilter("[Status] = 1")
            .HasDatabaseName("IX_CashSessions_Cashier_Open");
        builder.HasIndex(s => new { s.AgencyId, s.BusinessDate, s.Status });
    }
}

/// <summary>Cash movements: appended only, numbered per session (unique), linked to the payment or deposit that caused them.</summary>
internal sealed class CashMovementConfiguration : IEntityTypeConfiguration<CashMovement>
{
    public void Configure(EntityTypeBuilder<CashMovement> builder)
    {
        builder.ToTable("CashMovements", Schemas.Cash);
        builder.HasKey(m => m.Id);
        builder.Property(m => m.Reason).HasMaxLength(CashMovement.ReasonMaxLength);
        builder.Property(m => m.RecordedByName).HasMaxLength(AuditActor.UserNameMaxLength);

        builder.HasOne<Payment>().WithMany().HasForeignKey(m => m.PaymentId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<DepositHold>().WithMany().HasForeignKey(m => m.DepositId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<CashSession>().WithMany().HasForeignKey(m => m.RelatedSessionId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Expense>().WithMany().HasForeignKey(m => m.ExpenseId).OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(m => new { m.SessionId, m.Sequence }).IsUnique();
        builder.HasIndex(m => m.BookingId);
        builder.HasIndex(m => m.ExpenseId).HasFilter("[ExpenseId] IS NOT NULL");
    }
}
