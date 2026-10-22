using CarRental.Domain.Accounting;
using CarRental.Domain.Common;
using CarRental.Domain.Referential;
using CarRental.Infrastructure.Persistence.Auditing;
using CarRental.Infrastructure.Persistence.Conventions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CarRental.Infrastructure.Persistence.Configurations.Accounting;

/// <summary>
/// The charts of accounts of the agencies (temporal table <c>accounting.Accounts</c>, ADR-0067): one row per account of
/// an agency, its number unique in the agency.
/// </summary>
internal sealed class LedgerAccountConfiguration : IEntityTypeConfiguration<LedgerAccount>
{
    public void Configure(EntityTypeBuilder<LedgerAccount> builder)
    {
        builder.ToTemporalTable("Accounts", Schemas.Accounting);
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Number).HasMaxLength(LedgerAccount.NumberMaxLength).IsUnicode(false).IsRequired();
        builder.Property(a => a.Label).HasMaxLength(LedgerAccount.LabelMaxLength).IsRequired();
        builder.Property(a => a.ChangedBy).HasMaxLength(AuditActor.UserNameMaxLength);
        builder.Ignore(a => a.Class);
        builder.HasOne<Agency>().WithMany().HasForeignKey(a => a.AgencyId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(a => new { a.AgencyId, a.Number }).IsUnique();
    }
}

/// <summary>The mappings of the operation types of the agencies (temporal table <c>accounting.Mappings</c>): one row per agency and key.</summary>
internal sealed class AccountMappingConfiguration : IEntityTypeConfiguration<AccountMapping>
{
    public void Configure(EntityTypeBuilder<AccountMapping> builder)
    {
        builder.ToTemporalTable("Mappings", Schemas.Accounting);
        builder.HasKey(m => m.Id);
        builder.Property(m => m.Key).HasMaxLength(AccountMapping.KeyMaxLength).IsUnicode(false).IsRequired();
        builder.Property(m => m.AccountNumber).HasMaxLength(LedgerAccount.NumberMaxLength).IsUnicode(false).IsRequired();
        builder.Property(m => m.ChangedBy).HasMaxLength(AuditActor.UserNameMaxLength);
        builder.HasOne<Agency>().WithMany().HasForeignKey(m => m.AgencyId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(m => new { m.AgencyId, m.Key }).IsUnique();
        builder.HasIndex(m => new { m.AgencyId, m.AccountNumber });
    }
}

/// <summary>
/// The accounting months of the agencies (temporal table <c>accounting.Periods</c>, ADR-0065): one row per agency and month
/// once it was closed; the history keeps every closing and reopening.
/// </summary>
internal sealed class AccountingPeriodConfiguration : IEntityTypeConfiguration<AccountingPeriod>
{
    public void Configure(EntityTypeBuilder<AccountingPeriod> builder)
    {
        builder.ToTemporalTable("Periods", Schemas.Accounting);
        builder.HasKey(p => p.Id);
        builder.Property(p => p.ClosedByName).HasMaxLength(AuditActor.UserNameMaxLength);
        builder.Property(p => p.ReopenedByName).HasMaxLength(AuditActor.UserNameMaxLength);
        builder.Property(p => p.ReopenReason).HasMaxLength(AccountingPeriod.ReasonMaxLength);
        builder.Ignore(p => p.CalendarMonth);
        builder.Ignore(p => p.IsClosed);
        builder.HasOne<Agency>().WithMany().HasForeignKey(p => p.AgencyId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<PeriodClosing>().WithMany().HasForeignKey(p => p.CurrentClosingId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(p => new { p.AgencyId, p.Year, p.Month }).IsUnique();
    }
}

/// <summary>
/// The frozen journals of the closed months (<c>accounting.Closings</c>, their entries and lines): written once by a closing,
/// never updated. The entries and lines are not audited row by row (thousands of copies): the closing is, and the action
/// <c>accounting.period_closed</c> is recorded.
/// </summary>
internal sealed class PeriodClosingConfiguration : IEntityTypeConfiguration<PeriodClosing>
{
    public void Configure(EntityTypeBuilder<PeriodClosing> builder)
    {
        builder.ToTable("Closings", Schemas.Accounting);
        builder.HasKey(c => c.Id);
        builder.Property(c => c.ClosedByName).HasMaxLength(AuditActor.UserNameMaxLength);
        builder.Property(c => c.ConfirmedChecks).HasMaxLength(PeriodClosing.ConfirmedChecksMaxLength);
        builder.HasOne<Agency>().WithMany().HasForeignKey(c => c.AgencyId).OnDelete(DeleteBehavior.Restrict);
        builder.HasMany(c => c.Entries).WithOne().HasForeignKey(e => e.ClosingId).OnDelete(DeleteBehavior.Restrict);
        builder.Navigation(c => c.Entries).HasField("_entries").UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.HasIndex(c => new { c.AgencyId, c.Year, c.Month });
    }
}

internal sealed class JournalEntryConfiguration : IEntityTypeConfiguration<JournalEntry>
{
    public void Configure(EntityTypeBuilder<JournalEntry> builder)
    {
        builder.ToTable("JournalEntries", Schemas.Accounting);
        builder.HasKey(e => e.Id);
        builder.ExcludeFromAudit();
        builder.Property(e => e.Piece).HasMaxLength(JournalEntry.PieceMaxLength).IsRequired();
        builder.Property(e => e.Label).HasMaxLength(JournalEntry.LabelMaxLength).IsRequired();
        builder.Property(e => e.SourceKey).HasMaxLength(JournalEntry.SourceKeyMaxLength).IsUnicode(false).IsRequired();
        builder.Ignore(e => e.TotalDebit);
        builder.Ignore(e => e.TotalCredit);
        builder.HasMany(e => e.Lines).WithOne().HasForeignKey(l => l.EntryId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(e => e.Lines).HasField("_lines").UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.HasIndex(e => new { e.ClosingId, e.Sequence });
        builder.HasIndex(e => new { e.SourceKey, e.ClosingId });
    }
}

internal sealed class JournalLineConfiguration : IEntityTypeConfiguration<JournalLine>
{
    public void Configure(EntityTypeBuilder<JournalLine> builder)
    {
        builder.ToTable("JournalLines", Schemas.Accounting);
        builder.HasKey(l => l.Id);
        builder.ExcludeFromAudit();
        builder.Property(l => l.AccountNumber).HasMaxLength(LedgerAccount.NumberMaxLength).IsUnicode(false).IsRequired();
        builder.Property(l => l.Label).HasMaxLength(JournalEntry.LabelMaxLength).IsRequired();
        builder.Property(l => l.LetteringKey).HasMaxLength(JournalLine.LetteringKeyMaxLength);
        builder.Ignore(l => l.Balance);
        builder.HasIndex(l => new { l.EntryId, l.Position }).IsUnique();
    }
}

/// <summary>The justifications of the differences of the reconciliations (<c>accounting.Justifications</c>): only added.</summary>
internal sealed class ReconciliationJustificationConfiguration : IEntityTypeConfiguration<ReconciliationJustification>
{
    public void Configure(EntityTypeBuilder<ReconciliationJustification> builder)
    {
        builder.ToTable("Justifications", Schemas.Accounting);
        builder.HasKey(j => j.Id);
        builder.Property(j => j.DifferenceKey).HasMaxLength(ReconciliationJustification.KeyMaxLength).IsUnicode(false).IsRequired();
        builder.Property(j => j.Reason).HasMaxLength(ReconciliationJustification.ReasonMaxLength).IsRequired();
        builder.Property(j => j.JustifiedByName).HasMaxLength(AuditActor.UserNameMaxLength);
        builder.HasOne<Agency>().WithMany().HasForeignKey(j => j.AgencyId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(j => new { j.AgencyId, j.Year, j.Month, j.DifferenceKey });
    }
}

/// <summary>The merchant statements of Clic to Pay (<c>accounting.Statements</c>) and their lines, loaded with them.</summary>
internal sealed class MerchantStatementConfiguration : IEntityTypeConfiguration<MerchantStatement>
{
    public void Configure(EntityTypeBuilder<MerchantStatement> builder)
    {
        builder.ToTable("Statements", Schemas.Accounting);
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Provider).HasMaxLength(20).IsUnicode(false).IsRequired();
        builder.Property(s => s.Format).HasMaxLength(40).IsUnicode(false).IsRequired();
        builder.Property(s => s.FileName).HasMaxLength(MerchantStatement.FileNameMaxLength);
        builder.Property(s => s.ImportedByName).HasMaxLength(AuditActor.UserNameMaxLength);
        builder.HasOne<Agency>().WithMany().HasForeignKey(s => s.AgencyId).OnDelete(DeleteBehavior.Restrict);
        builder.HasMany(s => s.Lines).WithOne().HasForeignKey(l => l.StatementId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(s => s.Lines).HasField("_lines").UsePropertyAccessMode(PropertyAccessMode.Field).AutoInclude();
        builder.HasIndex(s => new { s.AgencyId, s.ImportedAtUtc });
    }
}

internal sealed class StatementLineConfiguration : IEntityTypeConfiguration<StatementLine>
{
    public void Configure(EntityTypeBuilder<StatementLine> builder)
    {
        builder.ToTable("StatementLines", Schemas.Accounting);
        builder.HasKey(l => l.Id);
        builder.Property(l => l.Reference).HasMaxLength(StatementLine.ReferenceMaxLength).IsUnicode(false).IsRequired();
        builder.Property(l => l.Note).HasMaxLength(StatementLine.NoteMaxLength);
        builder.Ignore(l => l.SignedAmount);
        builder.HasIndex(l => new { l.StatementId, l.Position }).IsUnique();
        builder.HasIndex(l => new { l.AgencyId, l.TransactionDate });
        builder.HasIndex(l => new { l.AgencyId, l.SettledOn }).HasFilter("[SettledOn] IS NOT NULL");
    }
}
