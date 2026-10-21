using CarRental.Domain.Billing;
using CarRental.Domain.Common;

namespace CarRental.Application.Billing.EInvoicing;

/// <summary>
/// Electronic invoicing of the billing documents (PROMPT §9.4, SPEC §8, ADR-0058): two regimes behind one port.
/// <see cref="EInvoiceMode.PaperTransitional"/> transmits nothing (the PDF carries the transitional mention, while the TTN
/// membership of the agency is pending); <see cref="EInvoiceMode.Ttn"/> produces the signed TEIF XML, transmits it to
/// Tunisie TradeNet and returns the unique reference TTN gives the document.
/// </summary>
/// <remarks>
/// TODO(spec): the TEIF schema, the signature provider (certificate) and the TTN transport are not delivered yet: the real
/// TTN adapter answers <c>billing.einvoice.not_configured</c> and a fake stands in for the tests. No format is invented.
/// A transmission is called from the outbox (never in a user transaction), retried on failure, and must be idempotent for
/// a given document number on the TTN side (TODO(spec): to check with the TTN documentation).
/// </remarks>
public interface IEInvoiceProvider
{
    EInvoiceMode Mode { get; }

    /// <summary>Transmits <paramref name="document"/> and returns the reference of TTN, or the reason of the failure.</summary>
    Task<Result<EInvoiceReceipt>> TransmitAsync(EInvoiceDocument document, CancellationToken cancellationToken);
}

/// <summary>What TTN answered for an accepted document: its unique reference and when it was accepted.</summary>
public sealed record EInvoiceReceipt(string Reference, DateTimeOffset AcceptedAtUtc);

/// <summary>
/// The data of a document for its electronic version: our own model (seller and buyer as frozen, lines, VAT per rate,
/// totals, stamp duty), which the TEIF generator will map once the official schema is provided — not a TEIF format.
/// </summary>
public sealed record EInvoiceDocument(
    BillingDocumentType Type,
    Guid DocumentId,
    string Number,
    DateOnly IssueDate,
    string? CorrectedInvoiceNumber,
    SellerIdentity Seller,
    BuyerIdentity Buyer,
    IReadOnlyList<EInvoiceLine> Lines,
    IReadOnlyList<VatRateTotal> VatTotals,
    Money TotalExclVat,
    Money TotalVat,
    Money TotalInclVat,
    Money StampDuty,
    Money TotalDue);

/// <summary>A line of the electronic document: label, quantity, unit price excluding VAT, HT, rate, VAT and TTC.</summary>
public sealed record EInvoiceLine(int Position, string Description, int Quantity, Money UnitPriceExclVat, Money ExclVat, decimal VatRate, Money Vat, Money InclVat);

/// <summary>The providers of the platform, one per regime (the Infrastructure registers the TTN one).</summary>
public sealed class EInvoiceProviders(IEnumerable<IEInvoiceProvider> providers)
{
    private readonly IReadOnlyList<IEInvoiceProvider> _providers = [.. providers];

    /// <summary>The provider of <paramref name="mode"/>; a missing TTN provider is a configuration error of the host.</summary>
    public IEInvoiceProvider For(EInvoiceMode mode) =>
        _providers.LastOrDefault(p => p.Mode == mode)
        ?? throw new InvalidOperationException($"No electronic invoicing provider is registered for {mode}.");
}

/// <summary>The transitional paper regime: nothing to transmit (its documents are never pending).</summary>
internal sealed class PaperTransitionalEInvoiceProvider : IEInvoiceProvider
{
    public EInvoiceMode Mode => EInvoiceMode.PaperTransitional;

    public Task<Result<EInvoiceReceipt>> TransmitAsync(EInvoiceDocument document, CancellationToken cancellationToken) =>
        Task.FromResult<Result<EInvoiceReceipt>>(BillingErrors.EInvoiceNotTtn);
}
