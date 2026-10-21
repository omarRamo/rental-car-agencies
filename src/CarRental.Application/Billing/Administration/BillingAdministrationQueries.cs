using CarRental.Application.Billing.Invoicing;
using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Common.Files;
using CarRental.Application.Common.Outbox;
using CarRental.Application.Common.Paging;
using CarRental.Application.Identity.Contracts;
using CarRental.Application.Payments.Contracts;
using CarRental.Application.Referential.Contracts;
using CarRental.Domain.Billing;
using CarRental.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace CarRental.Application.Billing.Administration;

/// <summary>
/// The invoices screens (<c>/admin/invoices</c>): lists of invoices and credit notes with their filters, the page of an
/// invoice, the files waiting for their invoice, the documents waiting for TTN, the stored PDFs and the retry of a failed
/// transmission. Reads need <c>invoices.read</c>; issuing <c>invoices.issue</c>; credit notes and retries
/// <c>invoices.manage</c> (the services check them again).
/// </summary>
public sealed class BillingAdministrationQueries
{
    /// <summary>How far back the files waiting for their invoice are looked for.</summary>
    public static readonly TimeSpan ToInvoiceWindow = TimeSpan.FromDays(400);

    private readonly BillingStore _store;
    private readonly IBookingsBillingQueries _bookings;
    private readonly IPaymentsBillingQueries _payments;
    private readonly IReferentialQueries _referential;
    private readonly IOutbox _outbox;
    private readonly IFileStorage _storage;

    internal BillingAdministrationQueries(
        BillingStore store,
        IBookingsBillingQueries bookings,
        IPaymentsBillingQueries payments,
        IReferentialQueries referential,
        IOutbox outbox,
        IFileStorage storage)
    {
        _store = store;
        _bookings = bookings;
        _payments = payments;
        _referential = referential;
        _outbox = outbox;
        _storage = storage;
    }

    /// <summary>The active agencies, by name (the first one is shown by default).</summary>
    public async Task<Result<IReadOnlyList<AgencyInfo>>> GetAgenciesAsync(CancellationToken cancellationToken = default)
    {
        if (!_store.User.HasPermission(Permissions.InvoicesRead))
        {
            return BillingErrors.Forbidden;
        }

        var agencies = await _referential.GetAgenciesAsync(cancellationToken);
        return Result.Success<IReadOnlyList<AgencyInfo>>([.. agencies.Where(a => a.IsActive).OrderBy(a => a.Name, StringComparer.Ordinal)]);
    }

    /// <summary>The invoices matching <paramref name="filter"/>, the most recent number first.</summary>
    public async Task<Result<PagedResult<InvoiceListItem>>> ListInvoicesAsync(InvoiceListFilter filter, PageRequest page, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(page);
        if (!_store.User.HasPermission(Permissions.InvoicesRead))
        {
            return BillingErrors.Forbidden;
        }

        var query = _store.Db.Set<Invoice>().AsNoTracking().IgnoreAutoIncludes();
        if (filter.AgencyId is { } agencyId)
        {
            query = query.Where(i => i.AgencyId == agencyId);
        }

        if (filter.From is { } from)
        {
            query = query.Where(i => i.IssueDate >= from);
        }

        if (filter.To is { } to)
        {
            query = query.Where(i => i.IssueDate <= to);
        }

        if (filter.Kind is { } kind)
        {
            query = query.Where(i => i.Kind == kind);
        }

        if (filter.EInvoiceStatus is { } status)
        {
            query = query.Where(i => i.EInvoiceStatus == status);
        }

        if (Clean(filter.Customer) is { } customer)
        {
            query = query.Where(i => i.Buyer.Name.Contains(customer) || (i.Buyer.TaxIdentifier != null && i.Buyer.TaxIdentifier.Contains(customer)));
        }

        if (Clean(filter.BookingNumber) is { } number)
        {
            var normalized = number.ToUpperInvariant();
            query = query.Where(i => (i.BookingNumber != null && i.BookingNumber.Contains(normalized)) || i.Number.Contains(normalized));
        }

        var ordered = query.OrderByDescending(i => i.FiscalYear).ThenByDescending(i => i.Sequence);

        // What remains is known once the credit notes are read: that filter pages in memory (a few thousand invoices a year).
        var total = filter.OnlyWithRemaining ? 0 : await ordered.CountAsync(cancellationToken);
        var paged = filter.OnlyWithRemaining ? ordered : ordered.Skip(page.Skip).Take(page.PageSize);
        var rows = await paged
            .Select(i => new
            {
                i.Id,
                i.Number,
                i.Kind,
                i.IssueDate,
                BuyerName = i.Buyer.Name,
                i.BookingId,
                i.BookingNumber,
                i.TotalInclVat,
                i.TotalDue,
                i.EInvoiceStatus,
                HasPdf = i.PdfPath != null,
            })
            .ToListAsync(cancellationToken);
        var credited = await CreditedAsync([.. rows.Select(r => r.Id)], cancellationToken);
        var items = rows
            .Select(r => new InvoiceListItem(r.Id, r.Number, r.Kind, r.IssueDate, r.BuyerName, r.BookingId, r.BookingNumber, r.TotalInclVat, r.TotalDue, credited.GetValueOrDefault(r.Id), r.EInvoiceStatus, r.HasPdf))
            .ToList();
        if (!filter.OnlyWithRemaining)
        {
            return new PagedResult<InvoiceListItem>(items, total, page.Page, page.PageSize);
        }

        var remaining = items.Where(i => i.Remaining.IsPositive).ToList();
        return new PagedResult<InvoiceListItem>([.. remaining.Skip(page.Skip).Take(page.PageSize)], remaining.Count, page.Page, page.PageSize);
    }

    /// <summary>The credit notes of an agency issued between two Tunis days, the most recent number first.</summary>
    public async Task<Result<PagedResult<CreditNoteListItem>>> ListCreditNotesAsync(
        Guid? agencyId,
        DateOnly? from,
        DateOnly? to,
        PageRequest page,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        if (!_store.User.HasPermission(Permissions.InvoicesRead))
        {
            return BillingErrors.Forbidden;
        }

        var query = _store.Db.Set<CreditNote>().AsNoTracking().IgnoreAutoIncludes();
        if (agencyId is { } agency)
        {
            query = query.Where(c => c.AgencyId == agency);
        }

        if (from is { } start)
        {
            query = query.Where(c => c.IssueDate >= start);
        }

        if (to is { } end)
        {
            query = query.Where(c => c.IssueDate <= end);
        }

        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(c => c.FiscalYear)
            .ThenByDescending(c => c.Sequence)
            .Skip(page.Skip)
            .Take(page.PageSize)
            .Select(c => new CreditNoteListItem(
                c.Id,
                c.Number,
                c.IssueDate,
                c.InvoiceId,
                c.InvoiceNumber,
                c.Scope,
                c.Reason,
                c.Buyer.Name,
                c.TotalInclVat,
                c.EInvoiceStatus,
                c.PdfPath != null))
            .ToListAsync(cancellationToken);
        return new PagedResult<CreditNoteListItem>(items, total, page.Page, page.PageSize);
    }

    /// <summary>The page of an invoice.</summary>
    public async Task<Result<InvoiceDetail>> GetInvoiceAsync(Guid invoiceId, CancellationToken cancellationToken = default)
    {
        if (!_store.User.HasPermission(Permissions.InvoicesRead))
        {
            return BillingErrors.Forbidden;
        }

        var invoice = await _store.Db.Set<Invoice>().AsNoTracking().Include(i => i.Lines).FirstOrDefaultAsync(i => i.Id == invoiceId, cancellationToken);
        if (invoice is null)
        {
            return BillingErrors.InvoiceNotFound;
        }

        var notes = await _store.Db.Set<CreditNote>().AsNoTracking().Include(c => c.Lines).Where(c => c.InvoiceId == invoiceId).ToListAsync(cancellationToken);
        var creditable = Crediting.LinesOf(invoice, notes);
        var remaining = Crediting.RemainingOf(invoice, notes);
        var canReadPayments = _store.User.HasPermission(Permissions.PaymentsRead);
        var payments = canReadPayments && invoice.BookingId is { } bookingId ? await _payments.GetBookingPaymentsAsync(bookingId, cancellationToken) : null;
        var manage = _store.User.HasPermission(Permissions.InvoicesManage);
        return new InvoiceDetail(
            invoice.Id,
            invoice.AgencyId,
            invoice.Number,
            invoice.Kind,
            invoice.IssueDate,
            invoice.IssuedAtUtc,
            invoice.IssuedBy,
            invoice.Seller,
            invoice.Buyer,
            invoice.BookingId,
            invoice.BookingNumber,
            invoice.FineId,
            invoice.ServiceFromUtc,
            invoice.ServiceToUtc,
            [
                .. creditable.Select(c => new InvoiceLineView(
                    c.Line.Id,
                    c.Line.Position,
                    c.Line.Nature,
                    c.Line.Description,
                    c.Line.Quantity,
                    c.Line.UnitPriceExclVat,
                    c.Line.ExclVat,
                    c.Line.VatRate,
                    c.Line.Vat,
                    c.Line.InclVat,
                    c.Remaining.InclVat)),
            ],
            invoice.VatBreakdown(),
            invoice.TotalExclVat,
            invoice.TotalVat,
            invoice.TotalInclVat,
            invoice.StampDuty,
            invoice.TotalDue,
            invoice.PaidAtIssue,
            invoice.DepositAppliedAtIssue,
            invoice.BalanceAtIssue,
            invoice.Mentions,
            invoice.EInvoiceMode,
            invoice.EInvoiceStatus,
            invoice.TtnReference,
            invoice.TransmittedAtUtc,
            invoice.TransmissionAttempts,
            invoice.LastTransmissionError,
            invoice.PdfPath is not null,
            invoice.EmailedTo,
            invoice.EmailedAtUtc,
            [
                .. notes.OrderBy(n => n.Sequence).Select(n => new CreditNoteView(
                    n.Id,
                    n.Number,
                    n.IssueDate,
                    n.Scope,
                    n.Reason,
                    n.TotalExclVat,
                    n.TotalVat,
                    n.TotalInclVat,
                    n.EInvoiceStatus,
                    n.TtnReference,
                    n.PdfPath is not null,
                    n.IssuedBy)),
            ],
            remaining,
            payments,
            new InvoiceActions(
                CanCredit: manage && remaining.IsPositive,
                CanRetryTransmission: manage && invoice.EInvoiceStatus == EInvoiceStatus.Failed,
                CanReadPayments: canReadPayments));
    }

    /// <summary>The invoices of a file (its page), by issue.</summary>
    public async Task<Result<IReadOnlyList<InvoiceListItem>>> GetInvoicesOfBookingAsync(Guid bookingId, CancellationToken cancellationToken = default)
    {
        if (!_store.User.HasPermission(Permissions.InvoicesRead))
        {
            return BillingErrors.Forbidden;
        }

        var rows = await _store.Db.Set<Invoice>()
            .AsNoTracking()
            .IgnoreAutoIncludes()
            .Where(i => i.BookingId == bookingId)
            .OrderBy(i => i.IssuedAtUtc)
            .Select(i => new { i.Id, i.Number, i.Kind, i.IssueDate, BuyerName = i.Buyer.Name, i.BookingNumber, i.TotalInclVat, i.TotalDue, i.EInvoiceStatus, HasPdf = i.PdfPath != null })
            .ToListAsync(cancellationToken);
        var credited = await CreditedAsync([.. rows.Select(r => r.Id)], cancellationToken);
        return Result.Success<IReadOnlyList<InvoiceListItem>>(
        [
            .. rows.Select(r => new InvoiceListItem(r.Id, r.Number, r.Kind, r.IssueDate, r.BuyerName, bookingId, r.BookingNumber, r.TotalInclVat, r.TotalDue, credited.GetValueOrDefault(r.Id), r.EInvoiceStatus, r.HasPdf)),
        ]);
    }

    /// <summary>
    /// The files of an agency waiting for their invoice (closed, or cancelled with a fee, over the last
    /// <see cref="ToInvoiceWindow"/>), with the reason when the seller identity of the agency blocks them.
    /// </summary>
    public async Task<Result<IReadOnlyList<FileToInvoice>>> GetFilesToInvoiceAsync(Guid agencyId, CancellationToken cancellationToken = default)
    {
        if (!_store.User.HasPermission(Permissions.InvoicesRead))
        {
            return BillingErrors.Forbidden;
        }

        var files = await _bookings.GetFilesToInvoiceAsync(agencyId, _store.Now() - ToInvoiceWindow, 500, cancellationToken);
        if (files.Count == 0)
        {
            return Result.Success<IReadOnlyList<FileToInvoice>>([]);
        }

        var ids = files.Select(f => f.BookingId).ToList();
        var invoiced = (await _store.Db.Set<Invoice>()
                .AsNoTracking()
                .IgnoreAutoIncludes()
                .Where(i => i.BookingId != null && ids.Contains(i.BookingId.Value) && (i.Kind == InvoiceKind.Rental || i.Kind == InvoiceKind.CancellationFee))
                .Select(i => new { BookingId = i.BookingId!.Value, i.Kind })
                .ToListAsync(cancellationToken))
            .Select(i => (i.BookingId, i.Kind))
            .ToHashSet();
        var seller = await _store.SellerAsync(agencyId, cancellationToken);
        var blocking = seller.IsFailure ? seller.Error.Code : null;
        return Result.Success<IReadOnlyList<FileToInvoice>>(
        [
            .. files
                .Select(f => (File: f, Kind: f.Status == BookingStatusInfo.Closed ? InvoiceKind.Rental : InvoiceKind.CancellationFee))
                .Where(x => !invoiced.Contains((x.File.BookingId, x.Kind)))
                .Select(x => new FileToInvoice(
                    x.File.BookingId,
                    x.File.Number,
                    x.Kind,
                    x.File.StatusChangedAtUtc,
                    x.Kind == InvoiceKind.Rental ? x.File.TotalInclVat : x.File.CancellationFee ?? Money.Zero,
                    blocking)),
        ]);
    }

    /// <summary>The invoices and credit notes of an agency not accepted by TTN yet (pending or failed), the oldest first.</summary>
    public async Task<Result<IReadOnlyList<EInvoiceQueueItem>>> GetEInvoiceQueueAsync(Guid? agencyId, CancellationToken cancellationToken = default)
    {
        if (!_store.User.HasPermission(Permissions.InvoicesRead))
        {
            return BillingErrors.Forbidden;
        }

        var invoices = _store.Db.Set<Invoice>().AsNoTracking().IgnoreAutoIncludes()
            .Where(i => i.EInvoiceStatus == EInvoiceStatus.Pending || i.EInvoiceStatus == EInvoiceStatus.Failed);
        var notes = _store.Db.Set<CreditNote>().AsNoTracking().IgnoreAutoIncludes()
            .Where(c => c.EInvoiceStatus == EInvoiceStatus.Pending || c.EInvoiceStatus == EInvoiceStatus.Failed);
        if (agencyId is { } agency)
        {
            invoices = invoices.Where(i => i.AgencyId == agency);
            notes = notes.Where(c => c.AgencyId == agency);
        }

        var invoiceItems = await invoices
            .Select(i => new EInvoiceQueueItem(BillingDocumentType.Invoice, i.Id, i.Number, i.IssueDate, i.Buyer.Name, i.TotalDue, i.EInvoiceStatus, i.TransmissionAttempts, i.LastTransmissionAttemptAtUtc, i.LastTransmissionError))
            .ToListAsync(cancellationToken);
        var noteItems = await notes
            .Select(c => new EInvoiceQueueItem(BillingDocumentType.CreditNote, c.Id, c.Number, c.IssueDate, c.Buyer.Name, c.TotalDue, c.EInvoiceStatus, c.TransmissionAttempts, c.LastTransmissionAttemptAtUtc, c.LastTransmissionError))
            .ToListAsync(cancellationToken);
        return Result.Success<IReadOnlyList<EInvoiceQueueItem>>([.. invoiceItems.Concat(noteItems).OrderBy(i => i.IssueDate).ThenBy(i => i.Number, StringComparer.Ordinal)]);
    }

    /// <summary>
    /// Asks for the steps of a document to run again (permission <c>invoices.manage</c>): a failed TTN transmission is
    /// tried again through the outbox.
    /// </summary>
    public async Task<Result> RetryTransmissionAsync(BillingDocumentType type, Guid documentId, CancellationToken cancellationToken = default)
    {
        if (!_store.User.HasPermission(Permissions.InvoicesManage))
        {
            return BillingErrors.Forbidden;
        }

        var status = type == BillingDocumentType.Invoice
            ? await _store.Db.Set<Invoice>().AsNoTracking().IgnoreAutoIncludes().Where(i => i.Id == documentId).Select(i => (EInvoiceStatus?)i.EInvoiceStatus).FirstOrDefaultAsync(cancellationToken)
            : await _store.Db.Set<CreditNote>().AsNoTracking().IgnoreAutoIncludes().Where(c => c.Id == documentId).Select(c => (EInvoiceStatus?)c.EInvoiceStatus).FirstOrDefaultAsync(cancellationToken);
        if (status is null)
        {
            return type == BillingDocumentType.Invoice ? BillingErrors.InvoiceNotFound : BillingErrors.CreditNoteNotFound;
        }

        if (status != EInvoiceStatus.Failed)
        {
            return BillingErrors.EInvoiceNotFailed;
        }

        _outbox.Enqueue(new BillingDocumentRetryRequested(type, documentId));
        await _store.Db.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    /// <summary>The stored PDF of a document (permission <c>invoices.read</c>).</summary>
    public async Task<Result<BillingPdfFile>> FindPdfAsync(BillingDocumentType type, Guid documentId, CancellationToken cancellationToken = default)
    {
        if (!_store.User.HasPermission(Permissions.InvoicesRead))
        {
            return BillingErrors.Forbidden;
        }

        var row = type == BillingDocumentType.Invoice
            ? await _store.Db.Set<Invoice>().AsNoTracking().IgnoreAutoIncludes().Where(i => i.Id == documentId).Select(i => new { i.Number, i.PdfPath, i.PdfHash }).FirstOrDefaultAsync(cancellationToken)
            : await _store.Db.Set<CreditNote>().AsNoTracking().IgnoreAutoIncludes().Where(c => c.Id == documentId).Select(c => new { c.Number, c.PdfPath, c.PdfHash }).FirstOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            return type == BillingDocumentType.Invoice ? BillingErrors.InvoiceNotFound : BillingErrors.CreditNoteNotFound;
        }

        return row is { PdfPath: { } path, PdfHash: { } hash }
            ? new BillingPdfFile(FileContainers.Invoices, path, Documents.BillingDocumentPipeline.PdfContentType, hash[..16], $"{row.Number}.pdf")
            : BillingErrors.PdfNotReady;
    }

    /// <summary>Opens a stored PDF.</summary>
    public Task<FileDownload?> OpenAsync(BillingPdfFile file, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        return _storage.OpenReadAsync(file.Container, file.Path, cancellationToken);
    }

    private async Task<Dictionary<Guid, Money>> CreditedAsync(IReadOnlyCollection<Guid> invoiceIds, CancellationToken cancellationToken)
    {
        var credited = new Dictionary<Guid, Money>();
        foreach (var chunk in invoiceIds.Chunk(500))
        {
            var rows = await _store.Db.Set<CreditNote>()
                .AsNoTracking()
                .IgnoreAutoIncludes()
                .Where(c => chunk.Contains(c.InvoiceId))
                .Select(c => new { c.InvoiceId, c.TotalInclVat })
                .ToListAsync(cancellationToken);
            foreach (var group in rows.GroupBy(r => r.InvoiceId))
            {
                credited[group.Key] = group.Select(r => r.TotalInclVat).Sum();
            }
        }

        return credited;
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
