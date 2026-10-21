using System.Security.Cryptography;
using CarRental.Application.Billing.EInvoicing;
using CarRental.Application.Common.Email;
using CarRental.Application.Common.Files;
using CarRental.Application.Common.Persistence;
using CarRental.Application.Referential.Contracts;
using CarRental.Domain.Billing;
using CarRental.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace CarRental.Application.Billing.Documents;

/// <summary>
/// What follows the issue of an invoice or a credit note, run from the outbox (ADR-0058), each step at most once and
/// replayable: (1) with TTN, the transmission of its electronic version — a failure is recorded on the document and
/// retried; (2) its PDF, stored in the private container <c>invoices</c> (<c>{year}/{number}.pdf</c>) once printable (on
/// paper at once, with TTN once its reference is known); (3) its e-mail to the buyer with the PDF attached by reference.
/// </summary>
public sealed class BillingDocumentPipeline
{
    public const string PdfContentType = "application/pdf";

    private readonly IAppDbContext _db;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IFileStorage _storage;
    private readonly IBillingDocumentRenderer _renderer;
    private readonly IBillingTexts _texts;
    private readonly IBillingEmailFactory _emails;
    private readonly IEmailQueue _emailQueue;
    private readonly EInvoiceProviders _providers;
    private readonly IReferentialQueries _referential;
    private readonly TimeProvider _clock;

    internal BillingDocumentPipeline(
        IAppDbContext db,
        IUnitOfWork unitOfWork,
        IFileStorage storage,
        IBillingDocumentRenderer renderer,
        IBillingTexts texts,
        IBillingEmailFactory emails,
        IEmailQueue emailQueue,
        EInvoiceProviders providers,
        IReferentialQueries referential,
        TimeProvider clock)
    {
        _db = db;
        _unitOfWork = unitOfWork;
        _storage = storage;
        _renderer = renderer;
        _texts = texts;
        _emails = emails;
        _emailQueue = emailQueue;
        _providers = providers;
        _referential = referential;
        _clock = clock;
    }

    /// <summary>
    /// Runs the steps due for the document. A failed TTN transmission is recorded on the document and returned as
    /// <c>billing.einvoice.transmission_failed</c> (the caller lets the outbox retry); an unknown document is ignored.
    /// </summary>
    public async Task<Result> RunAsync(BillingDocumentType type, Guid documentId, CancellationToken cancellationToken = default)
    {
        var document = await LoadAsync(type, documentId, tracked: false, cancellationToken);
        if (document is null)
        {
            return Result.Success();
        }

        if (document.EInvoiceMode == EInvoiceMode.Ttn && document.EInvoiceStatus != EInvoiceStatus.Transmitted)
        {
            var transmitted = await TransmitAsync(document, cancellationToken);
            if (transmitted.IsFailure)
            {
                return transmitted;
            }

            document = (await LoadAsync(type, documentId, tracked: false, cancellationToken))!;
        }

        if (document.PdfPath is null && document.IsReadyForPdf)
        {
            var stored = await StorePdfAsync(document, cancellationToken);
            if (stored.IsFailure)
            {
                return stored;
            }

            document = (await LoadAsync(type, documentId, tracked: false, cancellationToken))!;
        }

        if (document is { PdfPath: not null, EmailedAtUtc: null, Buyer.Email: not null })
        {
            return await EmailAsync(type, documentId, cancellationToken);
        }

        return Result.Success();
    }

    /// <summary>The PDF of a stored document, or a courtesy translation rendered now (never stored).</summary>
    public async Task<byte[]?> RenderAsync(BillingDocumentType type, Guid documentId, string language, CancellationToken cancellationToken = default)
    {
        var document = await LoadAsync(type, documentId, tracked: false, cancellationToken);
        return document is null ? null : _renderer.Render(BillingDocumentViews.Pdf(document, _texts, language));
    }

    private async Task<Result> TransmitAsync(BillingDocument document, CancellationToken cancellationToken)
    {
        var outcome = await _providers.For(EInvoiceMode.Ttn).TransmitAsync(BillingDocumentViews.Electronic(document), cancellationToken);
        var recorded = await _unitOfWork.ExecuteAsync(
            async ct =>
            {
                var tracked = await LoadAsync(document.BillingType, document.Id, tracked: true, ct);
                if (tracked is null)
                {
                    return Result.Success();
                }

                return outcome.IsSuccess
                    ? tracked.RecordTransmission(outcome.Value.Reference, outcome.Value.AcceptedAtUtc)
                    : tracked.RecordTransmissionFailure($"{outcome.Error.Code}: {outcome.Error.Message}", _clock.GetUtcNow());
            },
            cancellationToken: cancellationToken);
        if (recorded.IsFailure)
        {
            return recorded;
        }

        return outcome.IsSuccess ? Result.Success() : BillingErrors.EInvoiceTransmissionFailed;
    }

    private async Task<Result> StorePdfAsync(BillingDocument document, CancellationToken cancellationToken)
    {
        var pdf = _renderer.Render(BillingDocumentViews.Pdf(document, _texts));
        var path = BillingDocument.PdfPathOf(document.FiscalYear, document.Number);

        // The path is the number: a replay after a failure between the upload and the record writes the same file again.
        await using (var content = new MemoryStream(pdf, writable: false))
        {
            await _storage.UploadAsync(FileContainers.Invoices, path, content, PdfContentType, cancellationToken);
        }

        var hash = Convert.ToHexStringLower(SHA256.HashData(pdf));
        return await _unitOfWork.ExecuteAsync(
            async ct =>
            {
                var tracked = await LoadAsync(document.BillingType, document.Id, tracked: true, ct);
                if (tracked is null || tracked.PdfPath is not null)
                {
                    return Result.Success();
                }

                return tracked.AttachPdf(path, hash, pdf.LongLength, _clock.GetUtcNow());
            },
            cancellationToken: cancellationToken);
    }

    private async Task<Result> EmailAsync(BillingDocumentType type, Guid documentId, CancellationToken cancellationToken)
    {
        return await _unitOfWork.ExecuteAsync(
            async ct =>
            {
                var tracked = await LoadAsync(type, documentId, tracked: true, ct);
                if (tracked is not { PdfPath: { } path, EmailedAtUtc: null, Buyer.Email: { } to })
                {
                    return Result.Success();
                }

                var agency = (await _referential.GetAgenciesAsync(ct)).FirstOrDefault(a => a.Id == tracked.AgencyId);
                var message = _emails.Create(new BillingEmail(
                    tracked.BillingType,
                    to,
                    tracked.Buyer.Name,
                    tracked.Number,
                    tracked.IssueDate,
                    tracked.TotalDue,
                    agency?.Legal?.LegalName ?? agency?.Name ?? tracked.Seller.LegalName));
                await _emailQueue.EnqueueAsync(
                    message with { Attachments = [new EmailAttachment(FileContainers.Invoices, path, tracked.PdfFileName, PdfContentType)] },
                    ct);
                return tracked.MarkEmailed(to, _clock.GetUtcNow());
            },
            cancellationToken: cancellationToken);
    }

    private async Task<BillingDocument?> LoadAsync(BillingDocumentType type, Guid documentId, bool tracked, CancellationToken cancellationToken)
    {
        if (type == BillingDocumentType.Invoice)
        {
            var invoices = tracked ? _db.Set<Invoice>() : _db.Set<Invoice>().AsNoTracking();
            return await invoices.Include(i => i.Lines).FirstOrDefaultAsync(i => i.Id == documentId, cancellationToken);
        }

        var notes = tracked ? _db.Set<CreditNote>() : _db.Set<CreditNote>().AsNoTracking();
        return await notes.Include(c => c.Lines).FirstOrDefaultAsync(c => c.Id == documentId, cancellationToken);
    }
}
