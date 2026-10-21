using CarRental.Domain.Common;

namespace CarRental.Domain.Billing;

/// <summary>What an invoice refers to: the rental file (number, invoiced period, channel reference) or the fine re-invoiced.</summary>
public sealed record InvoiceSource(
    Guid? BookingId,
    string? BookingNumber,
    Guid? FineId,
    DateTimeOffset? ServiceFromUtc,
    DateTimeOffset? ServiceToUtc,
    string? ChannelReference = null)
{
    public const int BookingNumberMaxLength = 32;
    public const int ChannelReferenceMaxLength = 64;
}

/// <summary>
/// What the customer had paid when the invoice was issued: payments received net of refunds, and the deposit retained
/// to settle charges (a retention settles the damages invoiced; it is never revenue by itself, invariant 8).
/// </summary>
public sealed record InvoicePayments(Money Paid, Money DepositApplied)
{
    public static InvoicePayments None { get; } = new(Money.Zero, Money.Zero);
}

/// <summary>Everything an invoice is drafted from.</summary>
public sealed record InvoiceDraftRequest(
    Guid AgencyId,
    InvoiceKind Kind,
    SellerIdentity Seller,
    BuyerIdentity Buyer,
    InvoiceSource Source,
    IReadOnlyList<InvoiceLineInput> Lines,
    Money StampDuty,
    InvoicePayments Payments,
    IReadOnlyList<string> Mentions);

/// <summary>An invoice line with its VAT split: the input and its amounts.</summary>
public sealed record DraftLine(InvoiceLineInput Input, LineAmounts Amounts, Money UnitPriceExclVat);

/// <summary>
/// The draft of an invoice (state "Draft"): its lines split per VAT rate, its totals, the stamp duty outside the VAT, what
/// was paid and what remains due. Checked once; the number is taken only for a valid draft, inside the unit of work.
/// </summary>
public sealed class InvoiceDraft
{
    private InvoiceDraft(InvoiceDraftRequest request, IReadOnlyList<DraftLine> lines, IReadOnlyList<string> mentions)
    {
        Request = request;
        Lines = lines;
        Mentions = mentions;
        VatTotals = InvoiceTaxes.Breakdown(lines.Select(l => (l.Input.VatRate, l.Amounts)));
        TotalExclVat = lines.Select(l => l.Amounts.ExclVat).Sum();
        TotalVat = lines.Select(l => l.Amounts.Vat).Sum();
        TotalInclVat = lines.Select(l => l.Amounts.InclVat).Sum();
        TotalDue = TotalInclVat + request.StampDuty;
        Balance = TotalDue - request.Payments.Paid - request.Payments.DepositApplied;
    }

    public InvoiceDraftRequest Request { get; }

    public IReadOnlyList<DraftLine> Lines { get; }

    public IReadOnlyList<string> Mentions { get; }

    public IReadOnlyList<VatRateTotal> VatTotals { get; }

    public Money TotalExclVat { get; }

    public Money TotalVat { get; }

    public Money TotalInclVat { get; }

    public Money TotalDue { get; }

    /// <summary>What remains due at the issue (negative: paid in excess).</summary>
    public Money Balance { get; }

    public static Result<InvoiceDraft> Create(InvoiceDraftRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.AgencyId == Guid.Empty || !Enum.IsDefined(request.Kind) || request.Seller is null || request.Buyer is null || request.Source is null
            || request.Payments is null)
        {
            return BillingErrors.InvoiceLineInvalid;
        }

        if (request.Source.BookingNumber is { Length: > InvoiceSource.BookingNumberMaxLength }
            || request.Source.ChannelReference is { Length: > InvoiceSource.ChannelReferenceMaxLength })
        {
            return BillingErrors.PartyFieldTooLong;
        }

        if (request.Lines is null || request.Lines.Count == 0)
        {
            return BillingErrors.InvoiceNoLine;
        }

        if (request.Lines.Count > Invoice.MaxLines)
        {
            return BillingErrors.InvoiceTooManyLines;
        }

        if (request.Lines.Any(l => l is null || !l.IsValid()))
        {
            return BillingErrors.InvoiceLineInvalid;
        }

        if (request.StampDuty.IsNegative || request.StampDuty.Currency != Currency.Tnd)
        {
            return BillingErrors.InvoiceLineInvalid;
        }

        var mentions = BillingDocument.CleanMentions(request.Mentions);
        if (mentions.IsFailure)
        {
            return mentions.Error;
        }

        var amounts = InvoiceTaxes.Split(request.Lines);
        var lines = request.Lines
            .Select((line, i) => new DraftLine(line, amounts[i], InvoiceTaxes.UnitPriceExclVat(amounts[i].ExclVat, line.Quantity)))
            .ToList();
        var draft = new InvoiceDraft(request, lines, mentions.Value);
        return draft.TotalInclVat.IsNegative ? BillingErrors.InvoiceNegativeTotal : draft;
    }
}

/// <summary>
/// An invoice (facture), aggregate of the Billing module: numbered <c>FAC-AAAA-NNNNNN</c> without gap in its fiscal
/// year, issued once and never changed (PROMPT Â§6 invariant 6) â€” corrections are credit notes. Lines carry their
/// accounting nature, the VAT split per rate follows the pricing engine, the stamp duty is added outside the VAT.
/// </summary>
public sealed class Invoice : BillingDocument
{
    public const int MaxLines = 100;

    private readonly List<InvoiceLine> _lines = [];

    private Invoice()
    {
    }

    private Invoice(Guid id, InvoiceDraft draft, DocumentNumber number, DateTimeOffset issuedAt, BillingActor actor, EInvoiceMode mode)
        : base(id, draft.Request.AgencyId, number, issuedAt, actor, draft.Request.Seller, draft.Request.Buyer, mode, draft.Mentions)
    {
        var request = draft.Request;
        Kind = request.Kind;
        BookingId = request.Source.BookingId;
        BookingNumber = request.Source.BookingNumber;
        FineId = request.Source.FineId;
        ServiceFromUtc = request.Source.ServiceFromUtc?.ToUniversalTime();
        ServiceToUtc = request.Source.ServiceToUtc?.ToUniversalTime();
        ChannelReference = request.Source.ChannelReference;
        for (var i = 0; i < draft.Lines.Count; i++)
        {
            _lines.Add(new InvoiceLine(id, i + 1, draft.Lines[i]));
        }

        TotalExclVat = draft.TotalExclVat;
        TotalVat = draft.TotalVat;
        TotalInclVat = draft.TotalInclVat;
        StampDuty = request.StampDuty;
        TotalDue = draft.TotalDue;
        PaidAtIssue = request.Payments.Paid;
        DepositAppliedAtIssue = request.Payments.DepositApplied;
        BalanceAtIssue = draft.Balance;
    }

    public override BillingDocumentType BillingType => BillingDocumentType.Invoice;

    public InvoiceKind Kind { get; private set; }

    /// <summary>The fine re-invoiced, for <see cref="InvoiceKind.FineReinvoicing"/>.</summary>
    public Guid? FineId { get; private set; }

    /// <summary>Start of the invoiced service (actual pick-up of the rental).</summary>
    public DateTimeOffset? ServiceFromUtc { get; private set; }

    /// <summary>End of the invoiced service (actual return).</summary>
    public DateTimeOffset? ServiceToUtc { get; private set; }

    /// <summary>The reference of the booking at its sales channel (Booking.com), when any.</summary>
    public string? ChannelReference { get; private set; }

    /// <summary>Payments received net of refunds when the invoice was issued.</summary>
    public Money PaidAtIssue { get; private set; }

    /// <summary>Deposit retained to settle the charges, when the invoice was issued (a settlement, never revenue).</summary>
    public Money DepositAppliedAtIssue { get; private set; }

    /// <summary><see cref="BillingDocument.TotalDue"/> âˆ’ paid âˆ’ deposit applied, at the issue (negative: paid in excess).</summary>
    public Money BalanceAtIssue { get; private set; }

    public IReadOnlyList<InvoiceLine> Lines => [.. _lines.OrderBy(l => l.Position)];

    /// <summary>Totals per VAT rate, by ascending rate.</summary>
    public IReadOnlyList<VatRateTotal> VatTotals => InvoiceTaxes.Breakdown(_lines.Select(l => (l.VatRate, l.Amounts)));

    /// <summary>
    /// Issues <paramref name="draft"/> under <paramref name="number"/>, an invoice number of the Tunis year of
    /// <paramref name="issuedAt"/> taken in the same unit of work (ADR-0016). Raises <see cref="InvoiceIssued"/>.
    /// </summary>
    public static Result<Invoice> Issue(InvoiceDraft draft, DocumentNumber number, DateTimeOffset issuedAt, BillingActor actor, EInvoiceMode mode)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(actor);
        if (!Fits(number, DocumentType.Invoice, issuedAt))
        {
            return BillingErrors.NumberMismatch;
        }

        if (!Enum.IsDefined(mode))
        {
            return BillingErrors.EInvoiceNotTtn;
        }

        var invoice = new Invoice(NewId(), draft, number, issuedAt, actor, mode);
        invoice.Raise(new InvoiceIssued(invoice.Id, invoice.AgencyId, invoice.Number, invoice.Kind, invoice.BookingId, invoice.FineId, invoice.TotalDue, mode));
        return invoice;
    }
}

/// <summary>
/// A line of an invoice, frozen: accounting nature, source code, label (resource key with invariant arguments and the
/// agent's detail) and its text as issued, quantity, unit price as entered (VAT included) and excluding VAT, HT, VAT,
/// TTC, rate and how the amount was entered.
/// </summary>
public sealed class InvoiceLine : Entity
{
    private InvoiceLine()
    {
    }

    internal InvoiceLine(Guid invoiceId, int position, DraftLine line)
        : base(NewId())
    {
        InvoiceId = invoiceId;
        Position = position;
        Nature = line.Input.Nature;
        Code = line.Input.Code;
        LabelKey = line.Input.LabelKey;
        LabelArguments = [.. line.Input.LabelArguments];
        Detail = line.Input.Detail;
        Description = line.Input.Description.Trim();
        Quantity = line.Input.Quantity;
        UnitPrice = line.Input.UnitPrice;
        UnitPriceExclVat = line.UnitPriceExclVat;
        ExclVat = line.Amounts.ExclVat;
        Vat = line.Amounts.Vat;
        InclVat = line.Amounts.InclVat;
        VatRate = line.Input.VatRate;
        Basis = line.Input.Basis;
    }

    public Guid InvoiceId { get; private set; }

    /// <summary>Order of the line, from 1.</summary>
    public int Position { get; private set; }

    public InvoiceLineNature Nature { get; private set; }

    /// <summary>Code of the source line (quote line code, return charge kind, <c>CANCELLATION_FEE</c>â€¦).</summary>
    public string Code { get; private set; } = null!;

    public string LabelKey { get; private set; } = null!;

    public IReadOnlyList<string> LabelArguments { get; private set; } = [];

    /// <summary>The agent's description of a return charge, part of the label.</summary>
    public string? Detail { get; private set; }

    /// <summary>The label as printed on the original (in the language of the invoice).</summary>
    public string Description { get; private set; } = null!;

    public int Quantity { get; private set; }

    /// <summary>Unit price as entered, VAT included (indicative for a line entered excluding VAT).</summary>
    public Money UnitPrice { get; private set; }

    /// <summary>Unit price excluding VAT printed on the invoice (HT Ã· quantity, rounded to the millime).</summary>
    public Money UnitPriceExclVat { get; private set; }

    public Money ExclVat { get; private set; }

    public Money Vat { get; private set; }

    public Money InclVat { get; private set; }

    public decimal VatRate { get; private set; }

    public AmountBasis Basis { get; private set; }

    public LineAmounts Amounts => new(ExclVat, Vat, InclVat);
}
