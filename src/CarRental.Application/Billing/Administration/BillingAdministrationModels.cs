using CarRental.Application.Payments.Contracts;
using CarRental.Domain.Billing;
using CarRental.Domain.Common;

namespace CarRental.Application.Billing.Administration;

/// <summary>Filters of the list of invoices (Tunis issue dates, both included; texts match the start of a word or a number).</summary>
public sealed record InvoiceListFilter(
    Guid? AgencyId = null,
    DateOnly? From = null,
    DateOnly? To = null,
    string? Customer = null,
    string? BookingNumber = null,
    InvoiceKind? Kind = null,
    EInvoiceStatus? EInvoiceStatus = null,
    bool OnlyWithRemaining = false);

/// <summary>An invoice of the list: number, kind, date, buyer, file, totals, what credit notes took back, e-invoicing, PDF.</summary>
public sealed record InvoiceListItem(
    Guid Id,
    string Number,
    InvoiceKind Kind,
    DateOnly IssueDate,
    string BuyerName,
    Guid? BookingId,
    string? BookingNumber,
    Money TotalInclVat,
    Money TotalDue,
    Money Credited,
    EInvoiceStatus EInvoiceStatus,
    bool HasPdf)
{
    public Money Remaining => TotalInclVat + Credited;
}

/// <summary>A credit note of the list.</summary>
public sealed record CreditNoteListItem(
    Guid Id,
    string Number,
    DateOnly IssueDate,
    Guid InvoiceId,
    string InvoiceNumber,
    CreditNoteScope Scope,
    string Reason,
    string BuyerName,
    Money TotalInclVat,
    EInvoiceStatus EInvoiceStatus,
    bool HasPdf);

/// <summary>A file waiting for its invoice (closed, or cancelled with a fee) and why it was not issued automatically.</summary>
public sealed record FileToInvoice(
    Guid BookingId,
    string Number,
    InvoiceKind Kind,
    DateTimeOffset StatusChangedAtUtc,
    Money Amount,
    string? BlockingErrorCode);

/// <summary>A document whose electronic version is not accepted yet (pending or failed), with its last error.</summary>
public sealed record EInvoiceQueueItem(
    BillingDocumentType Type,
    Guid Id,
    string Number,
    DateOnly IssueDate,
    string BuyerName,
    Money TotalDue,
    EInvoiceStatus Status,
    int Attempts,
    DateTimeOffset? LastAttemptAtUtc,
    string? LastError);

/// <summary>A line of an invoice on its page, with what remains creditable.</summary>
public sealed record InvoiceLineView(
    Guid Id,
    int Position,
    InvoiceLineNature Nature,
    string Description,
    int Quantity,
    Money UnitPriceExclVat,
    Money ExclVat,
    decimal VatRate,
    Money Vat,
    Money InclVat,
    Money RemainingInclVat);

/// <summary>A credit note on the page of its invoice.</summary>
public sealed record CreditNoteView(
    Guid Id,
    string Number,
    DateOnly IssueDate,
    CreditNoteScope Scope,
    string Reason,
    Money TotalExclVat,
    Money TotalVat,
    Money TotalInclVat,
    EInvoiceStatus EInvoiceStatus,
    string? TtnReference,
    bool HasPdf,
    string IssuedBy);

/// <summary>The page of an invoice: the frozen document, its credit notes, what remains creditable and the payments of its file.</summary>
public sealed record InvoiceDetail(
    Guid Id,
    Guid AgencyId,
    string Number,
    InvoiceKind Kind,
    DateOnly IssueDate,
    DateTimeOffset IssuedAtUtc,
    string IssuedBy,
    SellerIdentity Seller,
    BuyerIdentity Buyer,
    Guid? BookingId,
    string? BookingNumber,
    Guid? FineId,
    DateTimeOffset? ServiceFromUtc,
    DateTimeOffset? ServiceToUtc,
    IReadOnlyList<InvoiceLineView> Lines,
    IReadOnlyList<VatRateTotal> VatTotals,
    Money TotalExclVat,
    Money TotalVat,
    Money TotalInclVat,
    Money StampDuty,
    Money TotalDue,
    Money PaidAtIssue,
    Money DepositAppliedAtIssue,
    Money BalanceAtIssue,
    IReadOnlyList<string> Mentions,
    EInvoiceMode EInvoiceMode,
    EInvoiceStatus EInvoiceStatus,
    string? TtnReference,
    DateTimeOffset? TransmittedAtUtc,
    int TransmissionAttempts,
    string? LastTransmissionError,
    bool HasPdf,
    string? EmailedTo,
    DateTimeOffset? EmailedAtUtc,
    IReadOnlyList<CreditNoteView> CreditNotes,
    Money Remaining,
    BookingBillingPayments? Payments,
    InvoiceActions Actions);

/// <summary>What the current user may do on an invoice page.</summary>
public sealed record InvoiceActions(bool CanCredit, bool CanRetryTransmission, bool CanReadPayments);

/// <summary>A stored PDF to stream: container, path, content type, entity tag and the file name offered.</summary>
public sealed record BillingPdfFile(string Container, string Path, string ContentType, string ETag, string FileName);
