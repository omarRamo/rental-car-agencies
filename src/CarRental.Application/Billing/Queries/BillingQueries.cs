using CarRental.Application.Billing.Contracts;
using CarRental.Application.Common.Persistence;
using CarRental.Application.Payments.Contracts;
using CarRental.Domain.Billing;
using CarRental.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace CarRental.Application.Billing.Queries;

/// <summary><see cref="IBillingQueries"/>: the issued documents read without tracking, mapped to the contract.</summary>
internal sealed class BillingQueries(IAppDbContext db, IPaymentsBillingQueries payments) : IBillingQueries
{
    public async Task<IReadOnlyList<SalesDocumentInfo>> GetSalesDocumentsAsync(Guid agencyId, DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
    {
        if (to < from)
        {
            return [];
        }

        var invoices = await db.Set<Invoice>()
            .AsNoTracking()
            .Include(i => i.Lines)
            .Where(i => i.AgencyId == agencyId && i.IssueDate >= from && i.IssueDate <= to)
            .ToListAsync(cancellationToken);
        var notes = await db.Set<CreditNote>()
            .AsNoTracking()
            .Include(c => c.Lines)
            .Where(c => c.AgencyId == agencyId && c.IssueDate >= from && c.IssueDate <= to)
            .ToListAsync(cancellationToken);
        return
        [
            .. invoices.OrderBy(i => i.FiscalYear).ThenBy(i => i.Sequence).Select(ToInfo),
            .. notes.OrderBy(c => c.FiscalYear).ThenBy(c => c.Sequence).Select(ToInfo),
        ];
    }

    public async Task<SalesDocumentInfo?> GetDocumentAsync(Guid documentId, CancellationToken cancellationToken = default)
    {
        var invoice = await db.Set<Invoice>().AsNoTracking().Include(i => i.Lines).FirstOrDefaultAsync(i => i.Id == documentId, cancellationToken);
        if (invoice is not null)
        {
            return ToInfo(invoice);
        }

        var note = await db.Set<CreditNote>().AsNoTracking().Include(c => c.Lines).FirstOrDefaultAsync(c => c.Id == documentId, cancellationToken);
        return note is null ? null : ToInfo(note);
    }

    public async Task<IReadOnlyList<InvoiceSummaryInfo>> GetInvoicesOfBookingAsync(Guid bookingId, CancellationToken cancellationToken = default)
    {
        var invoices = await db.Set<Invoice>()
            .AsNoTracking()
            .IgnoreAutoIncludes()
            .Where(i => i.BookingId == bookingId)
            .Select(i => new { i.Id, i.Number, i.Kind, i.IssueDate, i.IssuedAtUtc, i.TotalInclVat, i.TotalDue, i.EInvoiceStatus, HasPdf = i.PdfPath != null })
            .ToListAsync(cancellationToken);
        var ids = invoices.Select(i => i.Id).ToList();
        var notes = (await db.Set<CreditNote>()
                .AsNoTracking()
                .IgnoreAutoIncludes()
                .Where(c => ids.Contains(c.InvoiceId))
                .Select(c => new { c.Id, c.InvoiceId, c.Number, c.Sequence, c.IssueDate, c.TotalInclVat, c.Reason })
                .ToListAsync(cancellationToken))
            .ToLookup(c => c.InvoiceId);
        return
        [
            .. invoices.OrderBy(i => i.IssuedAtUtc).Select(i =>
            {
                var credit = notes[i.Id].OrderBy(c => c.Sequence).ToList();
                var credited = credit.Select(c => c.TotalInclVat).Sum();
                return new InvoiceSummaryInfo(
                    i.Id,
                    i.Number,
                    (InvoiceKindInfo)i.Kind,
                    i.IssueDate,
                    i.TotalInclVat,
                    i.TotalDue,
                    credited,
                    i.TotalInclVat + credited,
                    (EInvoiceStatusInfo)i.EInvoiceStatus,
                    i.HasPdf,
                    [.. credit.Select(c => new CreditNoteSummaryInfo(c.Id, c.Number, c.IssueDate, c.TotalInclVat, c.Reason))]);
            }),
        ];
    }

    public async Task<IReadOnlyList<CustomerOpenAmountInfo>> GetOpenAmountsAsync(Guid agencyId, CancellationToken cancellationToken = default)
    {
        var invoices = await db.Set<Invoice>()
            .AsNoTracking()
            .IgnoreAutoIncludes()
            .Where(i => i.AgencyId == agencyId)
            .Select(i => new { i.Id, i.BookingId, CustomerId = i.Buyer.CustomerId, BuyerName = i.Buyer.Name, i.TotalDue })
            .ToListAsync(cancellationToken);
        var credited = (await db.Set<CreditNote>()
                .AsNoTracking()
                .IgnoreAutoIncludes()
                .Where(c => c.AgencyId == agencyId)
                .Select(c => new { c.InvoiceId, c.TotalInclVat })
                .ToListAsync(cancellationToken))
            .GroupBy(c => c.InvoiceId)
            .ToDictionary(g => g.Key, g => g.Select(c => c.TotalInclVat).Sum());
        var bookingIds = invoices.Where(i => i.BookingId is not null).Select(i => i.BookingId!.Value).Distinct().ToList();
        var summaries = await payments.GetSummariesAsync(bookingIds, cancellationToken);

        // The money of a file settles the invoices of that file (payments net of refunds, deposits retained).
        var settledByBooking = summaries.ToDictionary(s => s.Key, s => s.Value.Payments.NetPaid + s.Value.Deposits.Retained);
        return
        [
            .. invoices
                .GroupBy(i => (i.CustomerId, Name: i.CustomerId is null ? i.BuyerName : string.Empty))
                .Select(g =>
                {
                    var invoiced = g.Select(i => i.TotalDue).Sum();
                    var credit = g.Select(i => credited.GetValueOrDefault(i.Id)).Sum();
                    var settled = g.Where(i => i.BookingId is not null).Select(i => i.BookingId!.Value).Distinct()
                        .Select(id => settledByBooking.GetValueOrDefault(id)).Sum();
                    return new CustomerOpenAmountInfo(g.Key.CustomerId, g.OrderByDescending(i => i.Id).First().BuyerName, invoiced, credit, settled, invoiced + credit - settled, g.Count());
                })
                .Where(o => !o.Open.IsZero)
                .OrderByDescending(o => o.Open)
                .ThenBy(o => o.BuyerName, StringComparer.Ordinal),
        ];
    }

    private static SalesDocumentInfo ToInfo(Invoice invoice) => new(
        invoice.Id,
        SalesDocumentTypeInfo.Invoice,
        invoice.Number,
        invoice.IssueDate,
        invoice.IssuedAtUtc,
        invoice.AgencyId,
        (InvoiceKindInfo)invoice.Kind,
        null,
        null,
        null,
        Buyer(invoice.Buyer),
        invoice.BookingId,
        invoice.BookingNumber,
        invoice.FineId,
        [
            .. invoice.Lines.Select(l => new SalesLineInfo(
                l.Id,
                l.Position,
                (InvoiceLineNatureInfo)l.Nature,
                l.Code,
                l.Description,
                l.Quantity,
                l.UnitPriceExclVat,
                l.ExclVat,
                l.VatRate,
                l.Vat,
                l.InclVat,
                null)),
        ],
        VatTotals(invoice.VatBreakdown()),
        invoice.TotalExclVat,
        invoice.TotalVat,
        invoice.TotalInclVat,
        invoice.StampDuty,
        invoice.TotalDue,
        new SalesPaymentSummaryInfo(invoice.PaidAtIssue, invoice.DepositAppliedAtIssue, invoice.BalanceAtIssue),
        EInvoice(invoice),
        invoice.PdfPath is not null);

    private static SalesDocumentInfo ToInfo(CreditNote note) => new(
        note.Id,
        SalesDocumentTypeInfo.CreditNote,
        note.Number,
        note.IssueDate,
        note.IssuedAtUtc,
        note.AgencyId,
        null,
        note.InvoiceId,
        note.InvoiceNumber,
        note.Reason,
        Buyer(note.Buyer),
        note.BookingId,
        note.BookingNumber,
        null,
        [
            .. note.Lines.Select(l => new SalesLineInfo(
                l.Id,
                l.Position,
                (InvoiceLineNatureInfo)l.Nature,
                l.Code,
                l.Description,
                1,
                l.ExclVat,
                l.ExclVat,
                l.VatRate,
                l.Vat,
                l.InclVat,
                l.InvoiceLineId)),
        ],
        VatTotals(note.VatBreakdown()),
        note.TotalExclVat,
        note.TotalVat,
        note.TotalInclVat,
        note.StampDuty,
        note.TotalDue,
        null,
        EInvoice(note),
        note.PdfPath is not null);

    private static BuyerInfo Buyer(BuyerIdentity buyer) =>
        new((BuyerKindInfo)buyer.Kind, buyer.CustomerId, buyer.Name, buyer.TaxIdentifier, buyer.AddressText);

    private static List<VatTotalInfo> VatTotals(IEnumerable<VatRateTotal> totals) =>
        [.. totals.Select(t => new VatTotalInfo(t.Rate, t.ExclVat, t.Vat, t.InclVat))];

    private static EInvoiceInfo EInvoice(BillingDocument document) =>
        new((EInvoiceModeInfo)document.EInvoiceMode, (EInvoiceStatusInfo)document.EInvoiceStatus, document.TtnReference, document.TransmittedAtUtc);
}
