using CarRental.Domain.Common;

namespace CarRental.Domain.Billing;

/// <summary>An amount of an invoice line to credit, VAT included (positive), in a partial credit note.</summary>
public sealed record CreditLineRequest(Guid InvoiceLineId, decimal Amount);

/// <summary>What to credit: everything that remains (<see cref="CreditNoteScope.Total"/>) or amounts of lines, and why.</summary>
public sealed record CreditNoteRequest(CreditNoteScope Scope, string? Reason, IReadOnlyList<CreditLineRequest>? Lines = null);

/// <summary>What remains creditable on an invoice line once the issued credit notes are deducted (VAT split included).</summary>
public sealed record CreditableLine(InvoiceLine Line, LineAmounts Credited, LineAmounts Remaining);

/// <summary>
/// What remains creditable on an invoice: per line and in total. Credit notes never take more than that, so that an
/// invoice and its credit notes never go below zero (PROMPT §6 invariant 6).
/// </summary>
public static class Crediting
{
    /// <summary>The lines of <paramref name="invoice"/> with what <paramref name="creditNotes"/> already credited (negative amounts).</summary>
    public static IReadOnlyList<CreditableLine> LinesOf(Invoice invoice, IEnumerable<CreditNote> creditNotes)
    {
        ArgumentNullException.ThrowIfNull(invoice);
        ArgumentNullException.ThrowIfNull(creditNotes);
        var credited = creditNotes
            .Where(c => c.InvoiceId == invoice.Id)
            .SelectMany(c => c.Lines)
            .GroupBy(l => l.InvoiceLineId)
            .ToDictionary(
                g => g.Key,
                g => new LineAmounts(g.Select(l => l.ExclVat).Sum(), g.Select(l => l.Vat).Sum(), g.Select(l => l.InclVat).Sum()));
        return
        [
            .. invoice.Lines.Select(line =>
            {
                var done = credited.GetValueOrDefault(line.Id);
                return new CreditableLine(
                    line,
                    done,
                    new LineAmounts(line.ExclVat + done.ExclVat, line.Vat + done.Vat, line.InclVat + done.InclVat));
            }),
        ];
    }

    /// <summary>What remains creditable on the whole invoice, VAT included: its total plus its (negative) credit notes.</summary>
    public static Money RemainingOf(Invoice invoice, IEnumerable<CreditNote> creditNotes)
    {
        ArgumentNullException.ThrowIfNull(invoice);
        ArgumentNullException.ThrowIfNull(creditNotes);
        return invoice.TotalInclVat + creditNotes.Where(c => c.InvoiceId == invoice.Id).Select(c => c.TotalInclVat).Sum();
    }
}

/// <summary>A credit note line before its issue: the invoice line it credits and the negative amounts.</summary>
public sealed record CreditDraftLine(InvoiceLine InvoiceLine, LineAmounts Amounts);

/// <summary>
/// The draft of a credit note (state "Draft"): negative lines tied to the invoice lines, never more than what remains
/// creditable — per line and on the whole invoice — and a mandatory reason. A total credit note takes exactly what remains
/// of every line (the invoice is then fully cancelled); a partial one credits amounts VAT included of positive lines, split
/// like the invoice line (proportionally to its HT), the last millimes of a line closing it exactly.
/// </summary>
public sealed class CreditNoteDraft
{
    public const int ReasonMinLength = 3;
    public const int ReasonMaxLength = 500;

    private CreditNoteDraft(
        Invoice invoice,
        CreditNoteScope scope,
        string reason,
        SellerIdentity seller,
        IReadOnlyList<CreditDraftLine> lines,
        IReadOnlyList<string> mentions)
    {
        Invoice = invoice;
        Scope = scope;
        Reason = reason;
        Seller = seller;
        Lines = lines;
        Mentions = mentions;
        TotalExclVat = lines.Select(l => l.Amounts.ExclVat).Sum();
        TotalVat = lines.Select(l => l.Amounts.Vat).Sum();
        TotalInclVat = lines.Select(l => l.Amounts.InclVat).Sum();
        VatTotals = InvoiceTaxes.Breakdown(lines.Select(l => (l.InvoiceLine.VatRate, l.Amounts)));
    }

    public Invoice Invoice { get; }

    public CreditNoteScope Scope { get; }

    public string Reason { get; }

    public SellerIdentity Seller { get; }

    public IReadOnlyList<CreditDraftLine> Lines { get; }

    public IReadOnlyList<string> Mentions { get; }

    public Money TotalExclVat { get; }

    public Money TotalVat { get; }

    /// <summary>Negative.</summary>
    public Money TotalInclVat { get; }

    public IReadOnlyList<VatRateTotal> VatTotals { get; }

    /// <summary>
    /// A credit note of <paramref name="invoice"/> given the credit notes already issued (<paramref name="existing"/>), the
    /// seller of the day and the mentions to print.
    /// </summary>
    public static Result<CreditNoteDraft> Create(
        Invoice invoice,
        IReadOnlyCollection<CreditNote> existing,
        CreditNoteRequest request,
        SellerIdentity seller,
        IReadOnlyList<string> mentions)
    {
        ArgumentNullException.ThrowIfNull(invoice);
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(seller);
        var reason = string.IsNullOrWhiteSpace(request.Reason) ? null : request.Reason.Trim();
        if (reason is not { Length: >= ReasonMinLength and <= ReasonMaxLength })
        {
            return BillingErrors.CreditNoteReasonRequired;
        }

        var cleanMentions = BillingDocument.CleanMentions(mentions);
        if (cleanMentions.IsFailure)
        {
            return cleanMentions.Error;
        }

        var remainingTotal = Crediting.RemainingOf(invoice, existing);
        if (!remainingTotal.IsPositive)
        {
            return BillingErrors.NothingToCredit;
        }

        var creditable = Crediting.LinesOf(invoice, existing);
        Result<IReadOnlyList<CreditDraftLine>> lines = request.Scope switch
        {
            CreditNoteScope.Total => Result.Success<IReadOnlyList<CreditDraftLine>>(
                [.. creditable.Where(c => !c.Remaining.InclVat.IsZero || !c.Remaining.ExclVat.IsZero).Select(c => new CreditDraftLine(c.Line, Negate(c.Remaining)))]),
            CreditNoteScope.Partial => Partial(creditable, request.Lines),
            _ => BillingErrors.CreditNoteNoLine,
        };
        if (lines.IsFailure)
        {
            return lines.Error;
        }

        var draft = new CreditNoteDraft(invoice, request.Scope, reason, seller, lines.Value, cleanMentions.Value);
        if (draft.Lines.Count == 0)
        {
            return BillingErrors.CreditNoteNoLine;
        }

        // Never more than what remains on the whole invoice (a partial credit on a damage capped by a negative line).
        return !draft.TotalInclVat.IsNegative || -draft.TotalInclVat > remainingTotal ? BillingErrors.CreditNoteExceedsInvoice : draft;
    }

    private static Result<IReadOnlyList<CreditDraftLine>> Partial(IReadOnlyList<CreditableLine> creditable, IReadOnlyList<CreditLineRequest>? requested)
    {
        if (requested is null || requested.Count == 0)
        {
            return BillingErrors.CreditNoteNoLine;
        }

        var lines = new List<CreditDraftLine>();
        var seen = new HashSet<Guid>();
        foreach (var entry in requested)
        {
            if (entry is null || !seen.Add(entry.InvoiceLineId) || creditable.FirstOrDefault(c => c.Line.Id == entry.InvoiceLineId) is not { } line)
            {
                return BillingErrors.CreditNoteLineUnknown;
            }

            if (!line.Line.InclVat.IsPositive)
            {
                return BillingErrors.CreditNoteLineNotCreditable;
            }

            if (Money.FromExact(entry.Amount, Currency.Tnd) is not { IsSuccess: true } exact
                || !exact.Value.IsPositive
                || exact.Value > line.Remaining.InclVat)
            {
                return BillingErrors.CreditNoteAmountInvalid;
            }

            lines.Add(new CreditDraftLine(line.Line, Negate(Split(line, exact.Value))));
        }

        // In the order of the invoice.
        return Result.Success<IReadOnlyList<CreditDraftLine>>([.. lines.OrderBy(l => l.InvoiceLine.Position)]);
    }

    /// <summary>
    /// The HT and VAT of <paramref name="amount"/> (VAT included, positive) credited on a line: the whole remainder closes
    /// the line exactly; a part is split like the line (HT proportional to its own HT), kept within what remains.
    /// </summary>
    private static LineAmounts Split(CreditableLine line, Money amount)
    {
        var remaining = line.Remaining;
        if (amount == remaining.InclVat)
        {
            return remaining;
        }

        var proportional = Money.Tnd(amount.Amount * line.Line.ExclVat.Amount / line.Line.InclVat.Amount);
        var lowest = Max(Money.Zero, amount - remaining.Vat);
        var highest = Min(amount, remaining.ExclVat);
        var exclVat = Min(Max(proportional, lowest), highest);
        return new LineAmounts(exclVat, amount - exclVat, amount);
    }

    private static LineAmounts Negate(LineAmounts amounts) => new(amounts.ExclVat.Negate(), amounts.Vat.Negate(), amounts.InclVat.Negate());

    private static Money Max(Money left, Money right) => left >= right ? left : right;

    private static Money Min(Money left, Money right) => left <= right ? left : right;
}

/// <summary>
/// A credit note (avoir), aggregate of the Billing module: numbered <c>AV-AAAA-NNNNNN</c> without gap in its fiscal year,
/// it corrects an issued invoice with negative lines tied to the invoice lines, states its reason, and is itself
/// immutable once issued. It bears no stamp duty (TODO(spec): to confirm with the chartered accountant).
/// </summary>
public sealed class CreditNote : BillingDocument
{
    private readonly List<CreditNoteLine> _lines = [];

    private CreditNote()
    {
    }

    private CreditNote(Guid id, CreditNoteDraft draft, DocumentNumber number, DateTimeOffset issuedAt, BillingActor actor, EInvoiceMode mode)
        : base(id, draft.Invoice.AgencyId, number, issuedAt, actor, draft.Seller, draft.Invoice.Buyer, mode, draft.Mentions)
    {
        InvoiceId = draft.Invoice.Id;
        InvoiceNumber = draft.Invoice.Number;
        InvoiceIssueDate = draft.Invoice.IssueDate;
        Scope = draft.Scope;
        Reason = draft.Reason;
        BookingId = draft.Invoice.BookingId;
        BookingNumber = draft.Invoice.BookingNumber;
        for (var i = 0; i < draft.Lines.Count; i++)
        {
            _lines.Add(new CreditNoteLine(id, i + 1, draft.Lines[i]));
        }

        TotalExclVat = draft.TotalExclVat;
        TotalVat = draft.TotalVat;
        TotalInclVat = draft.TotalInclVat;
        StampDuty = Money.Zero;
        TotalDue = draft.TotalInclVat;
    }

    public override BillingDocumentType BillingType => BillingDocumentType.CreditNote;

    public Guid InvoiceId { get; private set; }

    public string InvoiceNumber { get; private set; } = null!;

    public DateOnly InvoiceIssueDate { get; private set; }

    public CreditNoteScope Scope { get; private set; }

    public string Reason { get; private set; } = null!;

    public IReadOnlyList<CreditNoteLine> Lines => [.. _lines.OrderBy(l => l.Position)];

    /// <summary>Totals per VAT rate (negative), by ascending rate (computed from the lines).</summary>
    public IReadOnlyList<VatRateTotal> VatBreakdown() => InvoiceTaxes.Breakdown(_lines.Select(l => (l.VatRate, l.Amounts)));

    /// <summary>
    /// Issues <paramref name="draft"/> under <paramref name="number"/>, a credit note number of the Tunis year of
    /// <paramref name="issuedAt"/> taken in the same unit of work. Raises <see cref="CreditNoteIssued"/>.
    /// </summary>
    public static Result<CreditNote> Issue(CreditNoteDraft draft, DocumentNumber number, DateTimeOffset issuedAt, BillingActor actor, EInvoiceMode mode)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(actor);
        if (!Fits(number, DocumentType.CreditNote, issuedAt))
        {
            return BillingErrors.NumberMismatch;
        }

        if (!Enum.IsDefined(mode))
        {
            return BillingErrors.EInvoiceNotTtn;
        }

        var note = new CreditNote(NewId(), draft, number, issuedAt, actor, mode);
        note.Raise(new CreditNoteIssued(note.Id, note.InvoiceId, note.AgencyId, note.Number, note.InvoiceNumber, note.Scope, note.TotalInclVat, mode));
        return note;
    }
}

/// <summary>A line of a credit note: the invoice line it credits (copied label, nature, rate) and its negative amounts.</summary>
public sealed class CreditNoteLine : Entity
{
    private CreditNoteLine()
    {
    }

    internal CreditNoteLine(Guid creditNoteId, int position, CreditDraftLine line)
        : base(NewId())
    {
        CreditNoteId = creditNoteId;
        Position = position;
        InvoiceLineId = line.InvoiceLine.Id;
        Nature = line.InvoiceLine.Nature;
        Code = line.InvoiceLine.Code;
        LabelKey = line.InvoiceLine.LabelKey;
        LabelArguments = [.. line.InvoiceLine.LabelArguments];
        Detail = line.InvoiceLine.Detail;
        Description = line.InvoiceLine.Description;
        VatRate = line.InvoiceLine.VatRate;
        ExclVat = line.Amounts.ExclVat;
        Vat = line.Amounts.Vat;
        InclVat = line.Amounts.InclVat;
    }

    public Guid CreditNoteId { get; private set; }

    public int Position { get; private set; }

    /// <summary>The invoice line this line credits.</summary>
    public Guid InvoiceLineId { get; private set; }

    public InvoiceLineNature Nature { get; private set; }

    public string Code { get; private set; } = null!;

    public string LabelKey { get; private set; } = null!;

    public IReadOnlyList<string> LabelArguments { get; private set; } = [];

    public string? Detail { get; private set; }

    public string Description { get; private set; } = null!;

    public decimal VatRate { get; private set; }

    /// <summary>Negative.</summary>
    public Money ExclVat { get; private set; }

    /// <summary>Negative.</summary>
    public Money Vat { get; private set; }

    /// <summary>Negative.</summary>
    public Money InclVat { get; private set; }

    public LineAmounts Amounts => new(ExclVat, Vat, InclVat);
}
