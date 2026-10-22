using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Common.Files;
using CarRental.Application.Common.Persistence;
using CarRental.Domain.Billing;
using Microsoft.EntityFrameworkCore;

namespace CarRental.Application.Billing.Documents;

/// <summary>
/// The invoices and credit notes of a file in "Ma réservation" (ADR-0051, ADR-0058): those whose PDF is stored, the
/// latest first, read from the private invoices container (the French original; the English copy is a courtesy rendered
/// in the back office only). The caller already authorized the booking of the customer's session; a document id that is
/// not one of that booking's documents opens nothing.
/// </summary>
internal sealed class BillingCustomerDocumentProvider(IAppDbContext db, IFileStorage storage) : ICustomerDocumentProvider
{
    private const string InvoicePrefix = "invoice-";
    private const string CreditNotePrefix = "credit-note-";

    public async Task<IReadOnlyList<CustomerDocumentInfo>> GetDocumentsAsync(Guid bookingId, CancellationToken cancellationToken = default)
    {
        var invoices = await db.Set<Invoice>()
            .AsNoTracking()
            .Where(i => i.BookingId == bookingId && i.PdfPath != null)
            .Select(i => new { i.Id, i.Number, i.IssuedAtUtc })
            .ToListAsync(cancellationToken);
        var creditNotes = await db.Set<CreditNote>()
            .AsNoTracking()
            .Where(c => c.BookingId == bookingId && c.PdfPath != null)
            .Select(c => new { c.Id, c.Number, c.IssuedAtUtc })
            .ToListAsync(cancellationToken);
        return
        [
            .. invoices
                .Select(i => new CustomerDocumentInfo(InvoicePrefix + i.Id.ToString("N"), CustomerDocumentKind.Invoice, i.Number, i.IssuedAtUtc))
                .Concat(creditNotes.Select(c => new CustomerDocumentInfo(CreditNotePrefix + c.Id.ToString("N"), CustomerDocumentKind.CreditNote, c.Number, c.IssuedAtUtc)))
                .OrderByDescending(d => d.IssuedAtUtc),
        ];
    }

    public async Task<CustomerDocumentContent?> OpenAsync(Guid bookingId, string documentId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(documentId);
        var stored = await FindAsync(bookingId, documentId, cancellationToken);
        if (stored is not { PdfPath: { } path, Number: { } number })
        {
            return null;
        }

        var file = await storage.OpenReadAsync(FileContainers.Invoices, path, cancellationToken);
        return file is null ? null : new CustomerDocumentContent(file.Content, "application/pdf", $"{number}.pdf");
    }

    private async Task<(string? Number, string? PdfPath)?> FindAsync(Guid bookingId, string documentId, CancellationToken cancellationToken)
    {
        if (TryParse(documentId, InvoicePrefix) is { } invoiceId)
        {
            var invoice = await db.Set<Invoice>()
                .AsNoTracking()
                .Where(i => i.Id == invoiceId && i.BookingId == bookingId)
                .Select(i => new { i.Number, i.PdfPath })
                .FirstOrDefaultAsync(cancellationToken);
            return invoice is null ? null : (invoice.Number, invoice.PdfPath);
        }

        if (TryParse(documentId, CreditNotePrefix) is { } creditNoteId)
        {
            var creditNote = await db.Set<CreditNote>()
                .AsNoTracking()
                .Where(c => c.Id == creditNoteId && c.BookingId == bookingId)
                .Select(c => new { c.Number, c.PdfPath })
                .FirstOrDefaultAsync(cancellationToken);
            return creditNote is null ? null : (creditNote.Number, creditNote.PdfPath);
        }

        return null;
    }

    private static Guid? TryParse(string documentId, string prefix) =>
        documentId.StartsWith(prefix, StringComparison.Ordinal) && Guid.TryParseExact(documentId[prefix.Length..], "N", out var id)
            ? id
            : null;
}
