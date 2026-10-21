using CarRental.Application.Billing.EInvoicing;
using CarRental.Domain.Billing;

namespace CarRental.Application.Billing.Documents;

/// <summary>The PDF and the electronic version of an invoice or a credit note, from the frozen document.</summary>
internal static class BillingDocumentViews
{
    /// <summary>
    /// The PDF of a document: the original in French with the labels as issued, or a courtesy translation whose labels
    /// come from their resource keys in <paramref name="language"/>.
    /// </summary>
    public static BillingPdfDocument Pdf(BillingDocument document, IBillingTexts texts, string language = BillingDocument.DefaultLanguage)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(texts);
        var original = string.Equals(language, document.Language, StringComparison.Ordinal);
        return document switch
        {
            Invoice invoice => new BillingPdfDocument(
                BillingDocumentType.Invoice,
                invoice.Kind,
                invoice.Number,
                invoice.IssueDate,
                language,
                !original,
                invoice.Seller,
                invoice.Buyer,
                invoice.BookingNumber,
                invoice.ChannelReference,
                invoice.ServiceFromUtc,
                invoice.ServiceToUtc,
                null,
                null,
                null,
                [
                    .. invoice.Lines.Select(l => new BillingPdfLine(
                        l.Position,
                        l.Nature,
                        original ? l.Description : texts.LineLabel(l.LabelKey, l.LabelArguments, l.Detail, language),
                        l.Quantity,
                        l.UnitPriceExclVat,
                        l.ExclVat,
                        l.VatRate,
                        l.Vat,
                        l.InclVat)),
                ],
                invoice.VatBreakdown(),
                invoice.TotalExclVat,
                invoice.TotalVat,
                invoice.TotalInclVat,
                invoice.StampDuty,
                invoice.TotalDue,
                new BillingPdfPayments(invoice.PaidAtIssue, invoice.DepositAppliedAtIssue, invoice.BalanceAtIssue),
                invoice.Mentions,
                invoice.EInvoiceMode,
                invoice.TtnReference,
                invoice.TransmittedAtUtc,
                invoice.IssuedAtUtc,
                invoice.IssuedBy),
            CreditNote note => new BillingPdfDocument(
                BillingDocumentType.CreditNote,
                null,
                note.Number,
                note.IssueDate,
                language,
                !original,
                note.Seller,
                note.Buyer,
                note.BookingNumber,
                null,
                null,
                null,
                note.InvoiceNumber,
                note.InvoiceIssueDate,
                note.Reason,
                [
                    .. note.Lines.Select(l => new BillingPdfLine(
                        l.Position,
                        l.Nature,
                        original ? l.Description : texts.LineLabel(l.LabelKey, l.LabelArguments, l.Detail, language),
                        1,
                        l.ExclVat,
                        l.ExclVat,
                        l.VatRate,
                        l.Vat,
                        l.InclVat)),
                ],
                note.VatBreakdown(),
                note.TotalExclVat,
                note.TotalVat,
                note.TotalInclVat,
                note.StampDuty,
                note.TotalDue,
                null,
                note.Mentions,
                note.EInvoiceMode,
                note.TtnReference,
                note.TransmittedAtUtc,
                note.IssuedAtUtc,
                note.IssuedBy),
            _ => throw new ArgumentOutOfRangeException(nameof(document), document.GetType().Name, "Unknown billing document."),
        };
    }

    /// <summary>The data of a document for TTN (lines as issued, in French).</summary>
    public static EInvoiceDocument Electronic(BillingDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return document switch
        {
            Invoice invoice => new EInvoiceDocument(
                BillingDocumentType.Invoice,
                invoice.Id,
                invoice.Number,
                invoice.IssueDate,
                null,
                invoice.Seller,
                invoice.Buyer,
                [.. invoice.Lines.Select(l => new EInvoiceLine(l.Position, l.Description, l.Quantity, l.UnitPriceExclVat, l.ExclVat, l.VatRate, l.Vat, l.InclVat))],
                invoice.VatBreakdown(),
                invoice.TotalExclVat,
                invoice.TotalVat,
                invoice.TotalInclVat,
                invoice.StampDuty,
                invoice.TotalDue),
            CreditNote note => new EInvoiceDocument(
                BillingDocumentType.CreditNote,
                note.Id,
                note.Number,
                note.IssueDate,
                note.InvoiceNumber,
                note.Seller,
                note.Buyer,
                [.. note.Lines.Select(l => new EInvoiceLine(l.Position, l.Description, 1, l.ExclVat, l.ExclVat, l.VatRate, l.Vat, l.InclVat))],
                note.VatBreakdown(),
                note.TotalExclVat,
                note.TotalVat,
                note.TotalInclVat,
                note.StampDuty,
                note.TotalDue),
            _ => throw new ArgumentOutOfRangeException(nameof(document), document.GetType().Name, "Unknown billing document."),
        };
    }
}
