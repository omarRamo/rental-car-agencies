namespace CarRental.Application.Billing.Contracts;

/// <summary>
/// Public read API of the Billing module for the other modules (package 3B: sales journal, customer accounts, monthly
/// closing; the booking pages): the invoices and credit notes issued — frozen documents — with their lines, accounting
/// natures, VAT per rate, buyer, rental file, payment situation at the issue and electronic invoicing state.
/// </summary>
/// <remarks>
/// <para>No permission check here (like the other <c>I{Module}Queries</c>): the caller enforces its own.</para>
/// <para>Signs: an invoice has positive amounts (a discount or the franchise cap is a negative line), a credit note
/// negative ones; the net invoiced over a period is the sum of both. The stamp duty is outside the VAT and outside
/// <see cref="SalesDocumentInfo.TotalInclVat"/>; <see cref="SalesDocumentInfo.TotalDue"/> includes it. Dates are Tunis
/// calendar dates of issue.</para>
/// </remarks>
public interface IBillingQueries
{
    /// <summary>
    /// The invoices and credit notes issued by an agency between the Tunis days <paramref name="from"/> and
    /// <paramref name="to"/> (both included), invoices first then credit notes, each by number: the sales journal.
    /// </summary>
    Task<IReadOnlyList<SalesDocumentInfo>> GetSalesDocumentsAsync(Guid agencyId, DateOnly from, DateOnly to, CancellationToken cancellationToken = default);

    /// <summary>An invoice or a credit note with its lines, or <see langword="null"/>.</summary>
    Task<SalesDocumentInfo?> GetDocumentAsync(Guid documentId, CancellationToken cancellationToken = default);

    /// <summary>The invoices of a rental file (every kind), by issue, with what their credit notes took back.</summary>
    Task<IReadOnlyList<InvoiceSummaryInfo>> GetInvoicesOfBookingAsync(Guid bookingId, CancellationToken cancellationToken = default);

    /// <summary>
    /// What the customers of an agency owe now, per buyer of the invoices: invoiced (total due of the invoices), credited
    /// (credit notes, negative), settled (payments net of refunds and deposits retained, on the files of those invoices)
    /// and open = invoiced + credited − settled (negative: owed to the customer). Buyers whose open amount is zero are
    /// left out.
    /// </summary>
    Task<IReadOnlyList<CustomerOpenAmountInfo>> GetOpenAmountsAsync(Guid agencyId, CancellationToken cancellationToken = default);
}
