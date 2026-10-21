using CarRental.Application.Common.Email;
using CarRental.Application.Customers.Contracts;
using CarRental.Domain.Billing;
using CarRental.Domain.Common;

namespace CarRental.Application.Billing.Documents;

/// <summary>
/// Everything an invoice or a credit note PDF shows (ADR-0058): kind and number, issue date, seller and buyer as frozen,
/// the file and the invoiced period, the corrected invoice and the reason of a credit note, the lines, the VAT per rate,
/// the totals with the stamp duty, the payment situation at the issue, the mentions and the TTN reference when there is
/// one. Rendered by the web host (<see cref="IBillingDocumentRenderer"/>, QuestPDF). <c>Language</c> is <c>fr</c> for the
/// original, <c>en</c> for a courtesy translation (never stored).
/// </summary>
public sealed record BillingPdfDocument(
    BillingDocumentType Type,
    InvoiceKind? InvoiceKind,
    string Number,
    DateOnly IssueDate,
    string Language,
    bool IsCourtesyTranslation,
    SellerIdentity Seller,
    BuyerIdentity Buyer,
    string? BookingNumber,
    string? ChannelReference,
    DateTimeOffset? ServiceFromUtc,
    DateTimeOffset? ServiceToUtc,
    string? InvoiceNumber,
    DateOnly? InvoiceIssueDate,
    string? CreditReason,
    IReadOnlyList<BillingPdfLine> Lines,
    IReadOnlyList<VatRateTotal> VatTotals,
    Money TotalExclVat,
    Money TotalVat,
    Money TotalInclVat,
    Money StampDuty,
    Money TotalDue,
    BillingPdfPayments? Payments,
    IReadOnlyList<string> Mentions,
    EInvoiceMode EInvoiceMode,
    string? TtnReference,
    DateTimeOffset? TransmittedAtUtc,
    DateTimeOffset IssuedAtUtc,
    string IssuedBy);

/// <summary>A line of the PDF: label in the language of the document, quantity, unit price HT, HT, rate, VAT, TTC.</summary>
public sealed record BillingPdfLine(
    int Position,
    InvoiceLineNature Nature,
    string Label,
    int Quantity,
    Money UnitPriceExclVat,
    Money ExclVat,
    decimal VatRate,
    Money Vat,
    Money InclVat);

/// <summary>What was paid when the invoice was issued, what the deposit retention settled, and the balance then.</summary>
public sealed record BillingPdfPayments(Money Paid, Money DepositApplied, Money Balance);

/// <summary>Renders an invoice or a credit note as a PDF (implemented by the web host with its texts, QuestPDF).</summary>
public interface IBillingDocumentRenderer
{
    byte[] Render(BillingPdfDocument document);
}

/// <summary>
/// The texts of the billing documents that come from the resources of the web host: the label of a line from its key,
/// invariant arguments and the agent's detail (quote lines, return charges, billing lines), and the texts of the mentions
/// added by the platform. The label of an issued line is frozen in the language of the original.
/// </summary>
public interface IBillingTexts
{
    /// <summary>The label of a line in <paramref name="language"/> (<c>fr</c> or <c>en</c>).</summary>
    string LineLabel(string labelKey, IReadOnlyList<string> arguments, string? detail, string language);

    /// <summary>A text of the billing resources in <paramref name="language"/>, formatted with <paramref name="arguments"/>.</summary>
    string Text(string key, string language, params object[] arguments);
}

/// <summary>The e-mail carrying an invoice or a credit note to its buyer (the PDF is attached by reference by the caller).</summary>
public sealed record BillingEmail(
    BillingDocumentType Type,
    string To,
    string? ToName,
    string Number,
    DateOnly IssueDate,
    Money TotalDue,
    string AgencyName);

/// <summary>Writes the localized e-mail of a billing document (implemented by the web host with its resources).</summary>
public interface IBillingEmailFactory
{
    EmailMessage Create(BillingEmail email);
}

/// <summary>
/// The designation form of a traffic fine (fiche de désignation du conducteur, ADR-0059): the agency (owner of the
/// vehicle), the notice, the vehicle, the rental and the driver with the documents the authority needs (licence and
/// identity document, numbers in full: personal data, every production is logged). TODO(spec): the official form of
/// the authorities, if any, is not provided; this is the agency's own letter.
/// </summary>
public sealed record FineDesignationDocument(
    string AgencyLegalName,
    string? AgencyTaxIdentifier,
    string? AgencyAddress,
    string? AgencyPhone,
    string? AgencyEmail,
    string TicketNumber,
    string Authority,
    DateTimeOffset OffenceAtUtc,
    string Place,
    Money Amount,
    string PlateNumber,
    string? VehicleMake,
    string? VehicleModel,
    string BookingNumber,
    DateTimeOffset RentalFromUtc,
    DateTimeOffset? RentalUntilUtc,
    string DriverFullName,
    DateOnly? DriverBirthDate,
    string? DriverNationality,
    PostalAddressInfo? DriverAddress,
    IReadOnlyList<FineDesignationIdentityDocument> DriverDocuments,
    DateTimeOffset GeneratedAtUtc,
    string GeneratedBy);

/// <summary>A document of the designated driver as printed on the form.</summary>
public sealed record FineDesignationIdentityDocument(IdentityDocumentTypeInfo Type, string Number, string IssuingCountry, DateOnly? IssuedOn, DateOnly? ExpiresOn);

/// <summary>Renders the designation form of a fine as a PDF (implemented by the web host).</summary>
public interface IFineDesignationRenderer
{
    byte[] Render(FineDesignationDocument document);
}
