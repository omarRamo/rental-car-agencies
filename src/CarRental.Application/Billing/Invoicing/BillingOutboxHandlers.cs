using CarRental.Application.Billing.Contracts;
using CarRental.Application.Billing.Documents;
using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Common.Outbox;
using CarRental.Application.Referential.Contracts;
using CarRental.Domain.Billing;
using CarRental.Domain.Common;
using Microsoft.Extensions.Logging;

namespace CarRental.Application.Billing.Invoicing;

/// <summary>
/// The invoicing moments of a file (ADR-0057), from <see cref="BookingStatusChanged"/>: the final invoice at the closing,
/// the invoice of the cancellation fee at the cancellation — when the agency issues automatically
/// (<c>billing.issue_automatically</c>). Idempotent under the replays of the outbox. A file that cannot be invoiced
/// (incomplete seller identity, no payer…) is not retried: it waits in the "to invoice" list of the invoices screen.
/// </summary>
internal sealed partial class BookingInvoicingHandler(
    BillingScopes scopes,
    ISettingsReader settings,
    ILogger<BookingInvoicingHandler> logger) : IOutboxHandler<BookingStatusChanged>
{
    /// <summary>Errors that a retry would not fix: the file waits for an accountant (or needs nothing).</summary>
    private static readonly HashSet<string> Waiting = new(StringComparer.Ordinal)
    {
        BillingErrors.SellerIdentityIncomplete.Code,
        BillingErrors.BuyerMissing.Code,
        BillingErrors.BuyerNameRequired.Code,
        BillingErrors.BuyerTaxIdentifierRequired.Code,
        BillingErrors.BuyerAddressRequired.Code,
        BillingErrors.PartyFieldTooLong.Code,
        BillingErrors.EmailInvalid.Code,
        BillingErrors.NoCancellationFee.Code,
        BillingErrors.BookingNotClosed.Code,
        BillingErrors.BookingNotCancelled.Code,
        BillingErrors.BookingNotFound.Code,
        BillingErrors.InvoiceNoLine.Code,
        BillingErrors.InvoiceTooManyLines.Code,
        BillingErrors.InvoiceLineInvalid.Code,
        BillingErrors.InvoiceNegativeTotal.Code,
        BillingErrors.MentionInvalid.Code,
    };

    public async Task HandleAsync(BookingStatusChanged message, OutboxMessageContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(context);
        InvoiceKind? kind = message.Status switch
        {
            BookingStatusInfo.Closed => InvoiceKind.Rental,
            BookingStatusInfo.Cancelled => InvoiceKind.CancellationFee,
            _ => null,
        };
        if (kind is null || !await settings.GetAsync(BillingSettings.IssueAutomatically, message.AgencyId, cancellationToken))
        {
            return;
        }

        var issued = await scopes.RunAsync<InvoicingService, Result<IssuedDocument>>(
            (service, ct) => kind == InvoiceKind.Rental ? service.IssueRentalInvoiceAsync(message.BookingId, ct) : service.IssueCancellationInvoiceAsync(message.BookingId, ct),
            context.CorrelationId,
            cancellationToken);
        if (issued.IsSuccess)
        {
            return;
        }

        if (Waiting.Contains(issued.Error.Code))
        {
            if (issued.Error != BillingErrors.NoCancellationFee)
            {
                LogNotInvoiced(message.Number, issued.Error.Code);
            }

            return;
        }

        throw new BillingRetryException($"The invoicing of {message.Number} failed ({issued.Error.Code}); the outbox retries it.");
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "File {Number} was not invoiced ({ErrorCode}): it waits in the list of the files to invoice.")]
    private partial void LogNotInvoiced(string number, string errorCode);
}

/// <summary>An accountant asked for the steps of a document to run again (a failed TTN transmission, from the invoices screen).</summary>
public sealed record BillingDocumentRetryRequested(BillingDocumentType Type, Guid DocumentId);

/// <summary>
/// Runs <see cref="BillingDocumentPipeline"/> after the issue of an invoice or a credit note, and on a retry request: a
/// failed TTN transmission is thrown back to the outbox, which retries it with its back-off and lists it when it keeps
/// failing (the document shows its failure on the invoices screen).
/// </summary>
internal sealed class BillingDocumentHandler(BillingScopes scopes) :
    IOutboxHandler<InvoiceIssued>,
    IOutboxHandler<CreditNoteIssued>,
    IOutboxHandler<BillingDocumentRetryRequested>
{
    public Task HandleAsync(InvoiceIssued message, OutboxMessageContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(context);
        return RunAsync(BillingDocumentType.Invoice, message.InvoiceId, message.Number, context, cancellationToken);
    }

    public Task HandleAsync(CreditNoteIssued message, OutboxMessageContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(context);
        return RunAsync(BillingDocumentType.CreditNote, message.CreditNoteId, message.Number, context, cancellationToken);
    }

    public Task HandleAsync(BillingDocumentRetryRequested message, OutboxMessageContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(context);
        return RunAsync(message.Type, message.DocumentId, message.DocumentId.ToString("D"), context, cancellationToken);
    }

    private async Task RunAsync(BillingDocumentType type, Guid documentId, string number, OutboxMessageContext context, CancellationToken cancellationToken)
    {
        var outcome = await scopes.RunAsync<BillingDocumentPipeline, Result>((pipeline, ct) => pipeline.RunAsync(type, documentId, ct), context.CorrelationId, cancellationToken);
        if (outcome.IsFailure)
        {
            throw new BillingRetryException($"The steps of {number} did not complete ({outcome.Error.Code}); the outbox retries them.");
        }
    }
}
