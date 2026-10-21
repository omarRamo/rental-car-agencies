using CarRental.Domain.Cash;
using CarRental.Domain.Cash.Expenses;
using CarRental.Domain.Common;
using CarRental.Domain.Referential;
using CarRental.Infrastructure.Persistence.Auditing;
using CarRental.Infrastructure.Persistence.Conventions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CarRental.Infrastructure.Persistence.Configurations.Cash;

/// <summary>
/// Expenses (temporal table <c>cash.Expenses</c>, ADR-0063): one row per expense with its amounts, means of payment, links
/// (vehicle, maintenance, claim: ids only, the other modules are reached through their contracts) and receipt; deleted
/// logically (hidden by the soft delete filter, kept with the reason). Unique number per agency and year.
/// </summary>
internal sealed class ExpenseConfiguration : IEntityTypeConfiguration<Expense>
{
    public void Configure(EntityTypeBuilder<Expense> builder)
    {
        builder.ToTemporalTable("Expenses", Schemas.Cash);
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Number).HasMaxLength(Expense.NumberMaxLength).IsUnicode(false).IsRequired();
        builder.Property(e => e.Supplier).HasMaxLength(Expense.SupplierMaxLength).IsRequired();
        builder.Property(e => e.SupplierTaxId).HasMaxLength(Expense.TaxIdMaxLength);
        builder.Property(e => e.Reference).HasMaxLength(Expense.ReferenceMaxLength);
        builder.Property(e => e.Description).HasMaxLength(Expense.DescriptionMaxLength);
        builder.Property(e => e.PaymentReference).HasMaxLength(Expense.PaymentReferenceMaxLength);
        builder.Property(e => e.VatRate).HasPrecision(5, 4);
        builder.Property(e => e.ReceiptPath).HasMaxLength(Expense.ReceiptPathMaxLength).IsUnicode(false);
        builder.Property(e => e.ReceiptContentType).HasMaxLength(100).IsUnicode(false);
        builder.Property(e => e.ReceiptFileName).HasMaxLength(Expense.ReceiptFileNameMaxLength);
        builder.Property(e => e.ReceiptHash).HasMaxLength(64).IsFixedLength().IsUnicode(false);
        builder.Property(e => e.ReceiptUploadedBy).HasMaxLength(AuditActor.UserNameMaxLength);
        builder.Property(e => e.RecordedByName).HasMaxLength(AuditActor.UserNameMaxLength);
        builder.Property(e => e.ChangedByName).HasMaxLength(AuditActor.UserNameMaxLength);
        builder.Property(e => e.DeletedByName).HasMaxLength(AuditActor.UserNameMaxLength);
        builder.Property(e => e.DeletionReason).HasMaxLength(Expense.ReasonMaxLength);

        builder.Ignore(e => e.IsPaidInCash);
        builder.Ignore(e => e.HasReceipt);
        builder.Ignore(e => e.Amounts);
        builder.Ignore(e => e.VehicleCostRule);

        builder.HasOne<Agency>().WithMany().HasForeignKey(e => e.AgencyId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<CashSession>().WithMany().HasForeignKey(e => e.CashSessionId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<CashSession>().WithMany().HasForeignKey(e => e.ReversedInSessionId).OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(e => new { e.AgencyId, e.Year, e.Sequence }).IsUnique();
        builder.HasIndex(e => new { e.AgencyId, e.Date });
        builder.HasIndex(e => e.VehicleId).HasFilter("[VehicleId] IS NOT NULL");
    }
}

/// <summary>The counters of the numbers of the expenses, one per agency and year (a technical counter, not audited).</summary>
internal sealed class ExpenseSequenceConfiguration : IEntityTypeConfiguration<ExpenseSequence>
{
    public void Configure(EntityTypeBuilder<ExpenseSequence> builder)
    {
        builder.ToTable("ExpenseSequences", Schemas.Cash);
        builder.HasKey(s => s.Id);
        builder.ExcludeFromAudit();
        builder.HasOne<Agency>().WithMany().HasForeignKey(s => s.AgencyId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(s => new { s.AgencyId, s.Year }).IsUnique();
    }
}
