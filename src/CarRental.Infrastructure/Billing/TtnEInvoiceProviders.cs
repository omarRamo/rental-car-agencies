using System.Collections.Concurrent;
using System.Globalization;
using CarRental.Application.Billing.EInvoicing;
using CarRental.Domain.Billing;
using CarRental.Domain.Common;

namespace CarRental.Infrastructure.Billing;

/// <summary>
/// The Tunisie TradeNet adapter, empty on purpose (PROMPT §9.4 and §16, ADR-0058): the TEIF XML schema, the electronic
/// signature (certificate of the agency, signature provider) and the transmission to TTN (El Fatoora) are only delivered
/// with the membership of the agency. No format is invented here: every transmission answers
/// <c>billing.einvoice.not_configured</c>, so that an agency switched to TTN too early fails loudly (documents listed in
/// failure on the invoices screen, outbox retries) instead of exchanging guesses.
/// </summary>
/// <remarks>
/// TODO(spec): from the TTN documentation — the TEIF schema version and its mapping of our document (seller and buyer
/// identifiers, lines, VAT per rate, stamp duty, credit note reference), the XAdES signature with the certificate of the
/// agency (provider, PIN or HSM), the transport (endpoints, authentication, idempotency by document number), the reference
/// returned and the error codes; then the tests that run on the fake run on this adapter.
/// </remarks>
internal sealed class TtnEInvoiceProvider : IEInvoiceProvider
{
    public EInvoiceMode Mode => EInvoiceMode.Ttn;

    public Task<Result<EInvoiceReceipt>> TransmitAsync(EInvoiceDocument document, CancellationToken cancellationToken) =>
        Task.FromResult<Result<EInvoiceReceipt>>(BillingErrors.EInvoiceNotConfigured);
}

/// <summary>
/// TEST-ONLY stand-in of TTN (Development and Testing, <c>Billing:Ttn:UseFake</c>): it accepts every document and returns
/// a reference <c>TTN-FAKE-{number}</c>; the documents it received are listed. It never produces TEIF XML.
/// </summary>
public sealed class FakeTtnEInvoiceProvider(TimeProvider clock) : IEInvoiceProvider
{
    private readonly ConcurrentQueue<EInvoiceDocument> _received = new();

    public EInvoiceMode Mode => EInvoiceMode.Ttn;

    /// <summary>The documents transmitted to the fake, in their order.</summary>
    public IReadOnlyCollection<EInvoiceDocument> Received => _received;

    public Task<Result<EInvoiceReceipt>> TransmitAsync(EInvoiceDocument document, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        _received.Enqueue(document);
        var reference = string.Create(CultureInfo.InvariantCulture, $"TTN-FAKE-{document.Number}");
        return Task.FromResult<Result<EInvoiceReceipt>>(new EInvoiceReceipt(reference, clock.GetUtcNow()));
    }
}
