using CarRental.Domain.Billing;
using CarRental.Domain.Referential;
using CarRental.Infrastructure.Persistence.Auditing;
using CarRental.Infrastructure.Persistence.Conventions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CarRental.Infrastructure.Persistence.Configurations.Billing;

/// <summary>
/// Invoices (temporal table <c>billing.Invoices</c>, ADR-0057): the issued document in its row — number, dates, seller
/// and buyer frozen, totals, mentions — and its lines in <c>billing.InvoiceLines</c> (temporal), loaded with it. Nothing
/// issued is updated but the technical state (TTN, PDF, e-mail); the history tables keep every version. One final
/// invoice and one cancellation fee invoice per file (filtered unique index), one re-invoicing per fine.
/// </summary>
internal sealed class InvoiceConfiguration : IEntityTypeConfiguration<Invoice>
{
    public void Configure(EntityTypeBuilder<Invoice> builder)
    {
        builder.ToTemporalTable("Invoices", Schemas.Billing);
        builder.HasKey(i => i.Id);
        BillingDocumentColumns.Configure(builder);
        builder.Property(i => i.ChannelReference).HasMaxLength(InvoiceSource.ChannelReferenceMaxLength);

        builder.HasMany(i => i.Lines).WithOne().HasForeignKey(l => l.InvoiceId).OnDelete(DeleteBehavior.Restrict);
        builder.Navigation(i => i.Lines).HasField("_lines").UsePropertyAccessMode(PropertyAccessMode.Field).AutoInclude();

        // InvoiceKind.Rental = 1, InvoiceKind.CancellationFee = 2: one of each per file.
        builder.HasIndex(i => new { i.BookingId, i.Kind }).IsUnique().HasFilter("[BookingId] IS NOT NULL AND [Kind] IN (1, 2)");
        builder.HasIndex(i => i.FineId).IsUnique().HasFilter("[FineId] IS NOT NULL");
        builder.HasIndex(i => i.BookingId);
    }
}

/// <summary>The lines of the invoices (temporal), amounts in millimes, the VAT rate with four decimals.</summary>
internal sealed class InvoiceLineConfiguration : IEntityTypeConfiguration<InvoiceLine>
{
    public void Configure(EntityTypeBuilder<InvoiceLine> builder)
    {
        builder.ToTemporalTable("InvoiceLines", Schemas.Billing);
        builder.HasKey(l => l.Id);
        builder.Property(l => l.Code).HasMaxLength(InvoiceLineInput.CodeMaxLength).IsUnicode(false).IsRequired();
        builder.Property(l => l.LabelKey).HasMaxLength(InvoiceLineInput.LabelKeyMaxLength).IsUnicode(false).IsRequired();
        builder.PrimitiveCollection(l => l.LabelArguments).IsRequired();
        builder.Property(l => l.Detail).HasMaxLength(InvoiceLineInput.DetailMaxLength);
        builder.Property(l => l.Description).HasMaxLength(InvoiceLineInput.DescriptionMaxLength).IsRequired();
        builder.Property(l => l.VatRate).HasPrecision(5, 4);
        builder.Ignore(l => l.Amounts);
        builder.HasIndex(l => new { l.InvoiceId, l.Position }).IsUnique();
    }
}

/// <summary>Credit notes (temporal table <c>billing.CreditNotes</c>): like the invoices, tied to the invoice they correct.</summary>
internal sealed class CreditNoteConfiguration : IEntityTypeConfiguration<CreditNote>
{
    public void Configure(EntityTypeBuilder<CreditNote> builder)
    {
        builder.ToTemporalTable("CreditNotes", Schemas.Billing);
        builder.HasKey(c => c.Id);
        BillingDocumentColumns.Configure(builder);
        builder.Property(c => c.InvoiceNumber).HasMaxLength(BillingDocument.NumberMaxLength).IsUnicode(false).IsRequired();
        builder.Property(c => c.Reason).HasMaxLength(CreditNoteDraft.ReasonMaxLength).IsRequired();

        builder.HasOne<Invoice>().WithMany().HasForeignKey(c => c.InvoiceId).OnDelete(DeleteBehavior.Restrict);
        builder.HasMany(c => c.Lines).WithOne().HasForeignKey(l => l.CreditNoteId).OnDelete(DeleteBehavior.Restrict);
        builder.Navigation(c => c.Lines).HasField("_lines").UsePropertyAccessMode(PropertyAccessMode.Field).AutoInclude();
        builder.HasIndex(c => c.InvoiceId);
    }
}

/// <summary>The lines of the credit notes (temporal), each tied to the invoice line it credits.</summary>
internal sealed class CreditNoteLineConfiguration : IEntityTypeConfiguration<CreditNoteLine>
{
    public void Configure(EntityTypeBuilder<CreditNoteLine> builder)
    {
        builder.ToTemporalTable("CreditNoteLines", Schemas.Billing);
        builder.HasKey(l => l.Id);
        builder.Property(l => l.Code).HasMaxLength(InvoiceLineInput.CodeMaxLength).IsUnicode(false).IsRequired();
        builder.Property(l => l.LabelKey).HasMaxLength(InvoiceLineInput.LabelKeyMaxLength).IsUnicode(false).IsRequired();
        builder.PrimitiveCollection(l => l.LabelArguments).IsRequired();
        builder.Property(l => l.Detail).HasMaxLength(InvoiceLineInput.DetailMaxLength);
        builder.Property(l => l.Description).HasMaxLength(InvoiceLineInput.DescriptionMaxLength).IsRequired();
        builder.Property(l => l.VatRate).HasPrecision(5, 4);
        builder.Ignore(l => l.Amounts);
        builder.HasOne<InvoiceLine>().WithMany().HasForeignKey(l => l.InvoiceLineId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(l => new { l.CreditNoteId, l.Position }).IsUnique();
    }
}

/// <summary>The columns shared by invoices and credit notes.</summary>
internal static class BillingDocumentColumns
{
    public static void Configure<TDocument>(EntityTypeBuilder<TDocument> builder)
        where TDocument : BillingDocument
    {
        builder.Property(d => d.Number).HasMaxLength(BillingDocument.NumberMaxLength).IsUnicode(false).IsRequired();
        builder.Property(d => d.IssuedBy).HasMaxLength(BillingActor.NameMaxLength).IsRequired();
        builder.Property(d => d.Language).HasMaxLength(BillingDocument.LanguageMaxLength).IsUnicode(false).IsRequired();
        builder.Property(d => d.BookingNumber).HasMaxLength(InvoiceSource.BookingNumberMaxLength).IsUnicode(false);
        builder.PrimitiveCollection(d => d.Mentions).IsRequired();
        builder.Property(d => d.TtnReference).HasMaxLength(BillingDocument.TtnReferenceMaxLength);
        builder.Property(d => d.LastTransmissionError).HasMaxLength(BillingDocument.ErrorMaxLength);
        builder.Property(d => d.PdfPath).HasMaxLength(BillingDocument.PdfPathMaxLength).IsUnicode(false);
        builder.Property(d => d.PdfHash).HasMaxLength(BillingDocument.PdfHashLength).IsFixedLength().IsUnicode(false);
        builder.Property(d => d.EmailedTo).HasMaxLength(BuyerIdentity.EmailMaxLength).IsAuditSensitive();
        builder.Ignore(d => d.BillingType);
        builder.Ignore(d => d.PdfFileName);
        builder.Ignore(d => d.IsReadyForPdf);

        builder.ComplexProperty(d => d.Seller, seller =>
        {
            seller.Property(s => s.LegalName).HasColumnName("SellerLegalName").HasMaxLength(SellerIdentity.LegalNameMaxLength).IsRequired();
            seller.Property(s => s.TaxIdentifier).HasColumnName("SellerTaxIdentifier").HasMaxLength(SellerIdentity.TaxIdentifierMaxLength).IsRequired();
            seller.Property(s => s.TradeRegisterNumber).HasColumnName("SellerTradeRegisterNumber").HasMaxLength(SellerIdentity.TradeRegisterMaxLength);
            seller.Property(s => s.Address).HasColumnName("SellerAddress").HasMaxLength(SellerIdentity.AddressMaxLength).IsRequired();
            seller.Property(s => s.Phone).HasColumnName("SellerPhone").HasMaxLength(SellerIdentity.PhoneMaxLength);
            seller.Property(s => s.Email).HasColumnName("SellerEmail").HasMaxLength(SellerIdentity.EmailMaxLength);
            seller.Property(s => s.Footer).HasColumnName("SellerFooter").HasMaxLength(SellerIdentity.FooterMaxLength);
        });

        // The buyer is personal data for a person: masked in the audit log (the row keeps it, as the law requires).
        builder.ComplexProperty(d => d.Buyer, buyer =>
        {
            buyer.Property(b => b.Kind).HasColumnName("BuyerKind");
            buyer.Property(b => b.CustomerId).HasColumnName("BuyerCustomerId");
            buyer.Property(b => b.Name).HasColumnName("BuyerName").HasMaxLength(BuyerIdentity.NameMaxLength).IsRequired().HasAnnotation(AuditAnnotations.Sensitive, true);
            buyer.Property(b => b.TaxIdentifier).HasColumnName("BuyerTaxIdentifier").HasMaxLength(BuyerIdentity.TaxIdentifierMaxLength);
            buyer.Property(b => b.AddressLine1).HasColumnName("BuyerAddressLine1").HasMaxLength(BuyerIdentity.AddressLineMaxLength).HasAnnotation(AuditAnnotations.Sensitive, true);
            buyer.Property(b => b.AddressLine2).HasColumnName("BuyerAddressLine2").HasMaxLength(BuyerIdentity.AddressLineMaxLength).HasAnnotation(AuditAnnotations.Sensitive, true);
            buyer.Property(b => b.PostalCode).HasColumnName("BuyerPostalCode").HasMaxLength(BuyerIdentity.PostalCodeMaxLength).HasAnnotation(AuditAnnotations.Sensitive, true);
            buyer.Property(b => b.City).HasColumnName("BuyerCity").HasMaxLength(BuyerIdentity.CityMaxLength).HasAnnotation(AuditAnnotations.Sensitive, true);
            buyer.Property(b => b.Country).HasColumnName("BuyerCountry").HasMaxLength(BuyerIdentity.CountryLength).IsFixedLength().IsUnicode(false);
            buyer.Property(b => b.Email).HasColumnName("BuyerEmail").HasMaxLength(BuyerIdentity.EmailMaxLength).HasAnnotation(AuditAnnotations.Sensitive, true);
            buyer.Ignore(b => b.AddressText);
        });

        builder.HasOne<Agency>().WithMany().HasForeignKey(d => d.AgencyId).OnDelete(DeleteBehavior.Restrict);

        // Gapless numbers of each agency (ADR-0067): one document per number (and per position in its fiscal year) in an agency.
        builder.HasIndex(d => new { d.AgencyId, d.Number }).IsUnique();
        builder.HasIndex(d => new { d.AgencyId, d.FiscalYear, d.Sequence }).IsUnique();
        builder.HasIndex(d => new { d.AgencyId, d.IssueDate });

        // The documents waiting for TTN (EInvoiceStatus.Pending = 2, Failed = 4).
        builder.HasIndex(d => d.EInvoiceStatus).HasFilter("[EInvoiceStatus] IN (2, 4)");
    }
}

/// <summary>
/// Traffic fines (temporal table <c>billing.Fines</c>, ADR-0059): one per notice number and authority in an agency;
/// searched by plate key, by status and by offence date.
/// </summary>
internal sealed class FineConfiguration : IEntityTypeConfiguration<Fine>
{
    public void Configure(EntityTypeBuilder<Fine> builder)
    {
        builder.ToTemporalTable("Fines", Schemas.Billing);
        builder.HasKey(f => f.Id);
        builder.Property(f => f.TicketNumber).HasMaxLength(Fine.TicketMaxLength).IsRequired();
        builder.Property(f => f.Authority).HasMaxLength(Fine.AuthorityMaxLength).IsRequired();
        builder.Property(f => f.Place).HasMaxLength(Fine.PlaceMaxLength).IsRequired();
        builder.Property(f => f.PlateNumber).HasMaxLength(Fine.PlateMaxLength).IsRequired();
        builder.Property(f => f.PlateKey).HasMaxLength(Fine.PlateMaxLength).IsUnicode(false).IsRequired();
        builder.Property(f => f.Notes).HasMaxLength(Fine.NotesMaxLength);
        builder.Property(f => f.BookingNumber).HasMaxLength(Fine.BookingNumberMaxLength).IsUnicode(false);
        builder.Property(f => f.InvoiceNumber).HasMaxLength(BillingDocument.NumberMaxLength).IsUnicode(false);
        builder.Property(f => f.CloseNote).HasMaxLength(Fine.CloseNoteMaxLength);
        builder.Property(f => f.RecordedBy).HasMaxLength(BillingActor.NameMaxLength).IsRequired();
        builder.Property(f => f.DesignatedBy).HasMaxLength(BillingActor.NameMaxLength);
        builder.Property(f => f.StatusChangedBy).HasMaxLength(BillingActor.NameMaxLength).IsRequired();
        builder.PrimitiveCollection(f => f.CandidateBookingIds).IsRequired();
        builder.Ignore(f => f.HasRental);

        builder.HasOne<Agency>().WithMany().HasForeignKey(f => f.AgencyId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Invoice>().WithMany().HasForeignKey(f => f.InvoiceId).OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(f => new { f.AgencyId, f.Authority, f.TicketNumber }).IsUnique();
        builder.HasIndex(f => new { f.AgencyId, f.OffenceAtUtc });
        builder.HasIndex(f => new { f.AgencyId, f.Status });
        builder.HasIndex(f => f.PlateKey);
        builder.HasIndex(f => f.BookingId);
    }
}

/// <summary>
/// Claims (temporal table <c>billing.Claims</c>, ADR-0059) with their costs, reimbursements and files in child tables
/// loaded with them; the root's row version guards the children (its revision is bumped by every change).
/// </summary>
internal sealed class ClaimConfiguration : IEntityTypeConfiguration<Claim>
{
    public void Configure(EntityTypeBuilder<Claim> builder)
    {
        builder.ToTemporalTable("Claims", Schemas.Billing);
        builder.HasKey(c => c.Id);
        builder.Property(c => c.BookingNumber).HasMaxLength(Claim.BookingNumberMaxLength).IsUnicode(false);
        builder.Property(c => c.Place).HasMaxLength(Claim.PlaceMaxLength);
        builder.Property(c => c.Description).HasMaxLength(Claim.DescriptionMaxLength).IsRequired();
        builder.Property(c => c.Insurer).HasMaxLength(Claim.InsurerMaxLength);
        builder.Property(c => c.InsurerClaimNumber).HasMaxLength(Claim.ClaimNumberMaxLength);
        builder.Property(c => c.OpenedBy).HasMaxLength(BillingActor.NameMaxLength).IsRequired();
        builder.Property(c => c.LastChangedBy).HasMaxLength(BillingActor.NameMaxLength).IsRequired();
        builder.Property(c => c.Revision).IsAuditIgnored();
        builder.Ignore(c => c.TotalCosts);
        builder.Ignore(c => c.TotalReimbursements);
        builder.Ignore(c => c.NetCost);

        builder.HasOne<Agency>().WithMany().HasForeignKey(c => c.AgencyId).OnDelete(DeleteBehavior.Restrict);
        builder.HasMany(c => c.Costs).WithOne().HasForeignKey(k => k.ClaimId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(c => c.Costs).HasField("_costs").UsePropertyAccessMode(PropertyAccessMode.Field).AutoInclude();
        builder.HasMany(c => c.Reimbursements).WithOne().HasForeignKey(r => r.ClaimId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(c => c.Reimbursements).HasField("_reimbursements").UsePropertyAccessMode(PropertyAccessMode.Field).AutoInclude();
        builder.HasMany(c => c.Documents).WithOne().HasForeignKey(d => d.ClaimId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(c => c.Documents).HasField("_documents").UsePropertyAccessMode(PropertyAccessMode.Field).AutoInclude();

        builder.HasIndex(c => new { c.AgencyId, c.Status });
        builder.HasIndex(c => new { c.AgencyId, c.AccidentAtUtc });
        builder.HasIndex(c => c.VehicleId);
    }
}

internal sealed class ClaimCostConfiguration : IEntityTypeConfiguration<ClaimCost>
{
    public void Configure(EntityTypeBuilder<ClaimCost> builder)
    {
        builder.ToTemporalTable("ClaimCosts", Schemas.Billing);
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Label).HasMaxLength(Claim.LabelMaxLength).IsRequired();
        builder.Property(c => c.Reference).HasMaxLength(Claim.ReferenceMaxLength);
        builder.Property(c => c.RecordedBy).HasMaxLength(BillingActor.NameMaxLength).IsRequired();
        builder.HasIndex(c => new { c.ClaimId, c.Date });
    }
}

internal sealed class ClaimReimbursementConfiguration : IEntityTypeConfiguration<ClaimReimbursement>
{
    public void Configure(EntityTypeBuilder<ClaimReimbursement> builder)
    {
        builder.ToTemporalTable("ClaimReimbursements", Schemas.Billing);
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Label).HasMaxLength(Claim.LabelMaxLength).IsRequired();
        builder.Property(r => r.Reference).HasMaxLength(Claim.ReferenceMaxLength);
        builder.Property(r => r.RecordedBy).HasMaxLength(BillingActor.NameMaxLength).IsRequired();
        builder.HasIndex(r => new { r.ClaimId, r.Date });
    }
}

/// <summary>The files of the claims (references in the private container <c>inspections</c>, under <c>claims/</c>).</summary>
internal sealed class ClaimDocumentConfiguration : IEntityTypeConfiguration<ClaimDocument>
{
    public void Configure(EntityTypeBuilder<ClaimDocument> builder)
    {
        builder.ToTable("ClaimDocuments", Schemas.Billing);
        builder.HasKey(d => d.Id);
        builder.Property(d => d.FileName).HasMaxLength(Claim.FileNameMaxLength).IsRequired();
        builder.Property(d => d.StoragePath).HasMaxLength(Claim.PathMaxLength).IsUnicode(false).IsRequired();
        builder.Property(d => d.ContentType).HasMaxLength(Claim.ContentTypeMaxLength).IsUnicode(false).IsRequired();
        builder.Property(d => d.ContentHash).HasMaxLength(Claim.HashLength).IsFixedLength().IsUnicode(false).IsRequired();
        builder.Property(d => d.UploadedBy).HasMaxLength(BillingActor.NameMaxLength).IsRequired();
        builder.HasIndex(d => d.ClaimId);
    }
}
