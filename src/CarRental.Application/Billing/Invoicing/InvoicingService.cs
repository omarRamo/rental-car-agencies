using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Common.Numbering;
using CarRental.Application.Identity.Contracts;
using CarRental.Application.Payments.Contracts;
using CarRental.Domain.Billing;
using CarRental.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace CarRental.Application.Billing.Invoicing;

/// <summary>
/// Issues the invoices and the credit notes (ADR-0057). Every issue runs in one unit of work: the billing lock of the file
/// (or of the invoice), the idempotency check, the sources read fresh, the draft checked, then the gapless number taken
/// last (ADR-0016) — a refused issue gives its number back. The invoicing moments:
/// <list type="bullet">
/// <item>the closing of a file: its current quote and its return charges (franchise cap included), the payments received
/// and the deposit retained (a settlement, never revenue — invariant 8), one invoice per file;</item>
/// <item>the cancellation of a file with a fee: one invoice of the fee per file;</item>
/// <item>the re-invoicing of a traffic fine (optional, decided by the agency): the fine and the management fee.</item>
/// </list>
/// </summary>
public sealed class InvoicingService
{
    private readonly BillingStore _store;
    private readonly IBookingsBillingQueries _bookings;
    private readonly IHandoverQueries _handovers;
    private readonly IPaymentsQueries _payments;
    private readonly BuyerResolver _buyers;
    private readonly InvoiceComposer _composer;

    internal InvoicingService(
        BillingStore store,
        IBookingsBillingQueries bookings,
        IHandoverQueries handovers,
        IPaymentsQueries payments,
        BuyerResolver buyers,
        InvoiceComposer composer)
    {
        _store = store;
        _bookings = bookings;
        _handovers = handovers;
        _payments = payments;
        _buyers = buyers;
        _composer = composer;
    }

    /// <summary>
    /// The final invoice of a closed file (permission <c>invoices.issue</c>; the system at the closing). Idempotent: when the
    /// file already has it, it is returned (<see cref="IssuedDocument.AlreadyIssued"/>), never a second one.
    /// </summary>
    public async Task<Result<IssuedDocument>> IssueRentalInvoiceAsync(Guid bookingId, CancellationToken cancellationToken = default)
    {
        if (!_store.User.HasPermission(Permissions.InvoicesIssue))
        {
            return BillingErrors.Forbidden;
        }

        return await _store.UnitOfWork.ExecuteAsync<IssuedDocument>(
            async ct =>
            {
                var locked = await _store.Locks.AcquireAsync(BillingLocks.Booking(bookingId), cancellationToken: ct);
                if (locked.IsFailure)
                {
                    return locked.Error;
                }

                if (await _store.FindInvoiceOfBookingAsync(bookingId, InvoiceKind.Rental, ct) is { } existing)
                {
                    return new IssuedDocument(existing.Id, existing.Number, AlreadyIssued: true);
                }

                var facts = await _bookings.GetBillingFactsAsync(bookingId, ct);
                if (facts is null)
                {
                    return BillingErrors.BookingNotFound;
                }

                if (facts.Status != BookingStatusInfo.Closed)
                {
                    return BillingErrors.BookingNotClosed;
                }

                var charges = await _handovers.GetReturnChargesAsync(bookingId, ct);
                var lines = _composer.RentalLines(facts.Quote, charges);
                return await IssueAsync(facts, InvoiceKind.Rental, lines, fineId: null, ct);
            },
            cancellationToken: cancellationToken);
    }

    /// <summary>
    /// The invoice of the cancellation fee of a cancelled file (permission <c>invoices.issue</c>; the system at the
    /// cancellation). Idempotent like <see cref="IssueRentalInvoiceAsync"/>; a free or waived cancellation has nothing to invoice.
    /// </summary>
    public async Task<Result<IssuedDocument>> IssueCancellationInvoiceAsync(Guid bookingId, CancellationToken cancellationToken = default)
    {
        if (!_store.User.HasPermission(Permissions.InvoicesIssue))
        {
            return BillingErrors.Forbidden;
        }

        return await _store.UnitOfWork.ExecuteAsync<IssuedDocument>(
            async ct =>
            {
                var locked = await _store.Locks.AcquireAsync(BillingLocks.Booking(bookingId), cancellationToken: ct);
                if (locked.IsFailure)
                {
                    return locked.Error;
                }

                if (await _store.FindInvoiceOfBookingAsync(bookingId, InvoiceKind.CancellationFee, ct) is { } existing)
                {
                    return new IssuedDocument(existing.Id, existing.Number, AlreadyIssued: true);
                }

                var facts = await _bookings.GetBillingFactsAsync(bookingId, ct);
                if (facts is null)
                {
                    return BillingErrors.BookingNotFound;
                }

                if (facts.Status != BookingStatusInfo.Cancelled)
                {
                    return BillingErrors.BookingNotCancelled;
                }

                if (facts.CancellationFee is not { IsPositive: true } fee)
                {
                    return BillingErrors.NoCancellationFee;
                }

                var rate = await _store.VatRateAsync(facts.AgencyId, ct);
                return await IssueAsync(facts, InvoiceKind.CancellationFee, [_composer.CancellationFee(facts.Number, fee, rate)], fineId: null, ct);
            },
            cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Re-invoices a fine to the payer of its file (permissions <c>invoices.issue</c> and <c>fines.manage</c>): the fine at
    /// its amount and the management fee of the agency (<c>billing.fine_management_fee</c>). Once per fine.
    /// </summary>
    public async Task<Result<IssuedDocument>> ReinvoiceFineAsync(Guid fineId, CancellationToken cancellationToken = default)
    {
        if (!_store.User.HasPermission(Permissions.InvoicesIssue) || !_store.User.HasPermission(Permissions.FinesManage))
        {
            return BillingErrors.Forbidden;
        }

        return await _store.UnitOfWork.ExecuteAsync<IssuedDocument>(
            async ct =>
            {
                var locked = await _store.Locks.AcquireAsync(BillingLocks.Fine(fineId), cancellationToken: ct);
                if (locked.IsFailure)
                {
                    return locked.Error;
                }

                var fine = await _store.Db.Set<Fine>().FirstOrDefaultAsync(f => f.Id == fineId, ct);
                if (fine is null)
                {
                    return BillingErrors.FineNotFound;
                }

                if (fine.InvoiceId is { } invoiceId && fine.InvoiceNumber is { } invoiceNumber)
                {
                    return new IssuedDocument(invoiceId, invoiceNumber, AlreadyIssued: true);
                }

                if (fine.BookingId is not { } bookingId || !fine.HasRental)
                {
                    return BillingErrors.FineNoRental;
                }

                var facts = await _bookings.GetBillingFactsAsync(bookingId, ct);
                if (facts is null)
                {
                    return BillingErrors.BookingNotFound;
                }

                var fee = await _store.Settings.GetAsync(Contracts.BillingSettings.FineManagementFee, fine.AgencyId, ct);
                var rate = await _store.VatRateAsync(fine.AgencyId, ct);
                var issued = await IssueAsync(facts, InvoiceKind.FineReinvoicing, _composer.FineLines(fine, fee, rate), fine.Id, ct);
                if (issued.IsFailure)
                {
                    return issued;
                }

                var marked = fine.MarkReinvoiced(issued.Value.Id, issued.Value.Number, _store.Actor(), _store.Now());
                return marked.IsFailure ? marked.Error : issued;
            },
            cancellationToken: cancellationToken);
    }

    /// <summary>
    /// A credit note against an issued invoice (permission <c>invoices.manage</c>): total (everything that remains) or
    /// partial (amounts of lines), with a mandatory reason, never more than what remains creditable — checked under the
    /// lock of the invoice.
    /// </summary>
    public async Task<Result<IssuedDocument>> CreateCreditNoteAsync(Guid invoiceId, CreditNoteRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!_store.User.HasPermission(Permissions.InvoicesManage))
        {
            return BillingErrors.Forbidden;
        }

        return await _store.UnitOfWork.ExecuteAsync<IssuedDocument>(
            async ct =>
            {
                var locked = await _store.Locks.AcquireAsync(BillingLocks.Invoice(invoiceId), cancellationToken: ct);
                if (locked.IsFailure)
                {
                    return locked.Error;
                }

                var invoice = await _store.Db.Set<Invoice>().AsNoTracking().Include(i => i.Lines).FirstOrDefaultAsync(i => i.Id == invoiceId, ct);
                if (invoice is null)
                {
                    return BillingErrors.InvoiceNotFound;
                }

                var existing = await _store.Db.Set<CreditNote>().AsNoTracking().Include(c => c.Lines).Where(c => c.InvoiceId == invoiceId).ToListAsync(ct);
                var seller = await _store.SellerAsync(invoice.AgencyId, ct);
                if (seller.IsFailure)
                {
                    return seller.Error;
                }

                var mode = await _store.ModeAsync(invoice.AgencyId, ct);
                var mentions = await _store.MentionsAsync(invoice.AgencyId, mode, [], ct);
                var draft = CreditNoteDraft.Create(invoice, existing, request, seller.Value, mentions);
                if (draft.IsFailure)
                {
                    return draft.Error;
                }

                var now = _store.Now();
                var number = await _store.Numbers.NextForDateAsync(DocumentType.CreditNote, now, ct);
                var note = CreditNote.Issue(draft.Value, number, now, _store.Actor(), mode);
                if (note.IsFailure)
                {
                    return note.Error;
                }

                _store.Db.Set<CreditNote>().Add(note.Value);
                return new IssuedDocument(note.Value.Id, note.Value.Number, AlreadyIssued: false);
            },
            cancellationToken: cancellationToken);
    }

    /// <summary>Drafts and issues an invoice of a file: seller and buyer, payments, mentions, stamp duty, then the number.</summary>
    private async Task<Result<IssuedDocument>> IssueAsync(
        BookingBillingInfo facts,
        InvoiceKind kind,
        IReadOnlyList<InvoiceLineInput> lines,
        Guid? fineId,
        CancellationToken cancellationToken)
    {
        var seller = await _store.SellerAsync(facts.AgencyId, cancellationToken);
        if (seller.IsFailure)
        {
            return seller.Error;
        }

        var buyer = await _buyers.ResolveAsync(facts.PayerCustomerId, facts.MainDriverId, cancellationToken);
        if (buyer.IsFailure)
        {
            return buyer.Error;
        }

        // What the customer paid on the file; the deposit retained settles the charges of the final invoice only.
        var summary = await _payments.GetBookingSummaryAsync(facts.BookingId, cancellationToken);
        var payments = kind switch
        {
            InvoiceKind.Rental => new InvoicePayments(summary.Payments.NetPaid, summary.Deposits.Retained),
            InvoiceKind.CancellationFee => new InvoicePayments(summary.Payments.NetPaid, Money.Zero),
            _ => InvoicePayments.None,
        };

        var mode = await _store.ModeAsync(facts.AgencyId, cancellationToken);

        // TODO(spec): a file prepaid to Booking.com is invoiced to the payer recorded on the file; the invoicing rules of the
        // channel contract are not known yet. The reference of the channel is printed.
        IEnumerable<string> extra = facts is { Channel: SalesChannel.BookingCom, ChannelReference: { } reference }
            ? [_composer.ChannelMention(facts.Channel, reference)]
            : [];
        var mentions = await _store.MentionsAsync(facts.AgencyId, mode, extra, cancellationToken);
        var stampDuty = await _store.StampDutyAsync(facts.AgencyId, cancellationToken);
        var source = kind == InvoiceKind.Rental
            ? new InvoiceSource(facts.BookingId, facts.Number, fineId, facts.ActualPickupAt ?? facts.PickupAt, facts.ActualReturnAt ?? facts.ReturnAt, facts.ChannelReference)
            : new InvoiceSource(facts.BookingId, facts.Number, fineId, null, null, facts.ChannelReference);
        var draft = InvoiceDraft.Create(new InvoiceDraftRequest(
            facts.AgencyId,
            kind,
            seller.Value,
            buyer.Value,
            source,
            lines,
            stampDuty,
            payments,
            mentions));
        if (draft.IsFailure)
        {
            return draft.Error;
        }

        var now = _store.Now();
        var number = await _store.Numbers.NextForDateAsync(DocumentType.Invoice, now, cancellationToken);
        var invoice = Invoice.Issue(draft.Value, number, now, _store.Actor(), mode);
        if (invoice.IsFailure)
        {
            return invoice.Error;
        }

        _store.Db.Set<Invoice>().Add(invoice.Value);
        return new IssuedDocument(invoice.Value.Id, invoice.Value.Number, AlreadyIssued: false);
    }
}
