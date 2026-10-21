using CarRental.Domain.Common;

namespace CarRental.Application.Billing.Contracts;

/// <summary>Invoice or credit note (same values as the domain enum).</summary>
public enum SalesDocumentTypeInfo
{
    Invoice = 1,
    CreditNote = 2,
}

/// <summary>What an invoice bills (same values as the domain enum).</summary>
public enum InvoiceKindInfo
{
    /// <summary>Final invoice of a rental file, issued at its closing.</summary>
    Rental = 1,

    CancellationFee = 2,

    FineReinvoicing = 3,
}

/// <summary>
/// Accounting nature of a line (same values as the domain enum): what package 3B maps to an account of the chart of
/// accounts. <see cref="DailyContribution"/> is collected for the State; <see cref="FineReinvoicing"/> is a disbursement;
/// <see cref="Damage"/> is an indemnity (the deposit retained only settles it, invariant 8).
/// </summary>
public enum InvoiceLineNatureInfo
{
    Rental = 1,
    Discount = 2,
    Extras = 3,
    Protection = 4,
    Delivery = 5,
    DailyContribution = 6,
    ExtraMileage = 7,
    Fuel = 8,
    LateReturn = 9,
    Cleaning = 10,
    OtherCharge = 11,
    Damage = 12,
    CancellationFee = 13,
    FineReinvoicing = 14,
    ManagementFee = 15,
}

/// <summary>Person or company (same values as the domain enum).</summary>
public enum BuyerKindInfo
{
    Individual = 1,
    Company = 2,
}

/// <summary>Electronic invoicing regime of a document (same values as the domain enum).</summary>
public enum EInvoiceModeInfo
{
    PaperTransitional = 1,
    Ttn = 2,
}

/// <summary>Where a document stands with Tunisie TradeNet (same values as the domain enum).</summary>
public enum EInvoiceStatusInfo
{
    NotRequired = 1,
    Pending = 2,
    Transmitted = 3,
    Failed = 4,
}

/// <summary>The buyer frozen on a document: kind, payer id (Customers), name, tax identifier and address on one line.</summary>
public sealed record BuyerInfo(BuyerKindInfo Kind, Guid? CustomerId, string Name, string? TaxIdentifier, string? Address);

/// <summary>
/// A line of a document: accounting nature, source code, label as printed, quantity, unit price and amount excluding VAT,
/// VAT rate and amount, amount VAT included; a credit note line names the invoice line it credits.
/// </summary>
public sealed record SalesLineInfo(
    Guid Id,
    int Position,
    InvoiceLineNatureInfo Nature,
    string Code,
    string Description,
    int Quantity,
    Money UnitPriceExclVat,
    Money ExclVat,
    decimal VatRate,
    Money Vat,
    Money InclVat,
    Guid? InvoiceLineId);

/// <summary>The totals of one VAT rate of a document.</summary>
public sealed record VatTotalInfo(decimal Rate, Money ExclVat, Money Vat, Money InclVat);

/// <summary>What the customer had paid when the invoice was issued (net of refunds), what the deposit retention settled, and the balance then.</summary>
public sealed record SalesPaymentSummaryInfo(Money PaidAtIssue, Money DepositAppliedAtIssue, Money BalanceAtIssue);

/// <summary>The electronic invoicing state of a document.</summary>
public sealed record EInvoiceInfo(EInvoiceModeInfo Mode, EInvoiceStatusInfo Status, string? TtnReference, DateTimeOffset? TransmittedAtUtc);

/// <summary>
/// An issued invoice or credit note: identity and number, issue date (Tunis), what it bills or corrects, buyer, file,
/// lines, VAT per rate, totals (credit notes negative), the payment situation at the issue (invoices), the e-invoicing
/// state and whether its PDF is stored.
/// </summary>
public sealed record SalesDocumentInfo(
    Guid Id,
    SalesDocumentTypeInfo Type,
    string Number,
    DateOnly IssueDate,
    DateTimeOffset IssuedAtUtc,
    Guid AgencyId,
    InvoiceKindInfo? InvoiceKind,
    Guid? InvoiceId,
    string? InvoiceNumber,
    string? CreditReason,
    BuyerInfo Buyer,
    Guid? BookingId,
    string? BookingNumber,
    Guid? FineId,
    IReadOnlyList<SalesLineInfo> Lines,
    IReadOnlyList<VatTotalInfo> VatTotals,
    Money TotalExclVat,
    Money TotalVat,
    Money TotalInclVat,
    Money StampDuty,
    Money TotalDue,
    SalesPaymentSummaryInfo? Payments,
    EInvoiceInfo EInvoice,
    bool HasPdf);

/// <summary>A credit note of an invoice (negative total).</summary>
public sealed record CreditNoteSummaryInfo(Guid Id, string Number, DateOnly IssueDate, Money TotalInclVat, string Reason);

/// <summary>An invoice of a rental file and what its credit notes took back; <see cref="Remaining"/> = total VAT included + credited.</summary>
public sealed record InvoiceSummaryInfo(
    Guid Id,
    string Number,
    InvoiceKindInfo Kind,
    DateOnly IssueDate,
    Money TotalInclVat,
    Money TotalDue,
    Money Credited,
    Money Remaining,
    EInvoiceStatusInfo EInvoiceStatus,
    bool HasPdf,
    IReadOnlyList<CreditNoteSummaryInfo> CreditNotes);

/// <summary>What a buyer owes the agency now (negative <see cref="Open"/>: owed to the buyer).</summary>
public sealed record CustomerOpenAmountInfo(
    Guid? CustomerId,
    string BuyerName,
    Money Invoiced,
    Money Credited,
    Money Settled,
    Money Open,
    int InvoiceCount);
