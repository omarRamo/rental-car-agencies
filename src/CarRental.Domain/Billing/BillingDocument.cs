using System.Globalization;
using CarRental.Domain.Common;

namespace CarRental.Domain.Billing;

/// <summary>
/// What invoices and credit notes share (PROMPT Â§6 invariant 6): a gapless number of the fiscal year, an issue date in
/// Tunis, the seller and the buyer frozen at the issue, totals, the mentions printed, and the technical state that follows
/// the issue â€” the electronic invoicing (TTN), the stored PDF and the e-mail to the buyer.
/// </summary>
/// <remarks>
/// <para>An issued document is immutable: its number, dates, parties, lines, totals and mentions never change; a
/// correction is a credit note. Only the technical state changes afterwards, each step at most once: the TTN reference
/// (or the failures of its transmission), the reference of the stored PDF, the e-mail.</para>
/// <para>The draft state is <see cref="InvoiceDraft"/> / <see cref="CreditNoteDraft"/>, a value computed and checked before the
/// number is taken: a document is persisted issued, never as a draft.</para>
/// </remarks>
public abstract class BillingDocument : AggregateRoot, IAgencyOwned
{
    public const int NumberMaxLength = 32;
    public const int LanguageMaxLength = 8;
    public const int MentionMaxLength = 1000;
    public const int MaxMentions = 10;
    public const int TtnReferenceMaxLength = 100;
    public const int ErrorMaxLength = 500;
    public const int PdfPathMaxLength = 300;
    public const int PdfHashLength = 64;

    /// <summary>The language of the documents: French, the language of the commercial documents of the agency.</summary>
    public const string DefaultLanguage = "fr";

    private List<string> _mentions = [];

    /// <summary>Used by EF Core when materializing.</summary>
    protected BillingDocument()
    {
    }

    protected BillingDocument(
        Guid id,
        Guid agencyId,
        DocumentNumber number,
        DateTimeOffset issuedAt,
        BillingActor actor,
        SellerIdentity seller,
        BuyerIdentity buyer,
        EInvoiceMode mode,
        IReadOnlyList<string> mentions)
        : base(id)
    {
        ArgumentNullException.ThrowIfNull(number);
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(seller);
        ArgumentNullException.ThrowIfNull(buyer);
        ArgumentNullException.ThrowIfNull(mentions);
        AgencyId = agencyId;
        Number = number.Value;
        FiscalYear = number.Year;
        Sequence = number.Sequence;
        IssuedAtUtc = issuedAt.ToUniversalTime();
        IssueDate = TunisTime.DateOf(issuedAt);
        IssuedByUserId = actor.UserId;
        IssuedBy = actor.Name;
        Language = DefaultLanguage;
        Seller = seller;
        Buyer = buyer;
        EInvoiceMode = mode;
        EInvoiceStatus = mode == EInvoiceMode.Ttn ? EInvoiceStatus.Pending : EInvoiceStatus.NotRequired;
        _mentions = [.. mentions];
    }

    /// <summary>Invoice or credit note.</summary>
    public abstract BillingDocumentType BillingType { get; }

    public Guid AgencyId { get; private set; }

    /// <summary><c>FAC-AAAA-NNNNNN</c> or <c>AV-AAAA-NNNNNN</c>, unique, gapless in its fiscal year.</summary>
    public string Number { get; private set; } = null!;

    /// <summary>Fiscal year of the number: the Tunis year of <see cref="IssueDate"/>.</summary>
    public int FiscalYear { get; private set; }

    /// <summary>Position of the number in its fiscal year, from 1.</summary>
    public long Sequence { get; private set; }

    /// <summary>Date of issue, in Tunis.</summary>
    public DateOnly IssueDate { get; private set; }

    public DateTimeOffset IssuedAtUtc { get; private set; }

    public Guid? IssuedByUserId { get; private set; }

    public string IssuedBy { get; private set; } = null!;

    /// <summary>Language of the original document (<see cref="DefaultLanguage"/>).</summary>
    public string Language { get; private set; } = DefaultLanguage;

    public SellerIdentity Seller { get; private set; } = null!;

    public BuyerIdentity Buyer { get; private set; } = null!;

    /// <summary>The rental file the document belongs to, when any.</summary>
    public Guid? BookingId { get; protected set; }

    public string? BookingNumber { get; protected set; }

    public Money TotalExclVat { get; protected set; }

    public Money TotalVat { get; protected set; }

    /// <summary>Total of the lines, VAT included (stamp duty excluded).</summary>
    public Money TotalInclVat { get; protected set; }

    /// <summary>Stamp duty of the document, outside the VAT (<c>billing.stamp_duty</c>); none on a credit note.</summary>
    public Money StampDuty { get; protected set; }

    /// <summary><see cref="TotalInclVat"/> + <see cref="StampDuty"/>.</summary>
    public Money TotalDue { get; protected set; }

    /// <summary>The mentions printed on the document, frozen at its issue (transitional regime, legal mentionsâ€¦).</summary>
    public IReadOnlyList<string> Mentions
    {
        get => _mentions;
        private set => _mentions = [.. value];
    }

    public EInvoiceMode EInvoiceMode { get; private set; }

    public EInvoiceStatus EInvoiceStatus { get; private set; }

    /// <summary>The unique reference returned by TTN.</summary>
    public string? TtnReference { get; private set; }

    public DateTimeOffset? TransmittedAtUtc { get; private set; }

    public int TransmissionAttempts { get; private set; }

    public DateTimeOffset? LastTransmissionAttemptAtUtc { get; private set; }

    /// <summary>Error code and message of the last failed transmission (no personal data).</summary>
    public string? LastTransmissionError { get; private set; }

    /// <summary>Path of the PDF in the private container <c>invoices</c> (<c>{year}/{number}.pdf</c>).</summary>
    public string? PdfPath { get; private set; }

    /// <summary>SHA-256 of the PDF, hexadecimal.</summary>
    public string? PdfHash { get; private set; }

    public long? PdfLength { get; private set; }

    public DateTimeOffset? PdfStoredAtUtc { get; private set; }

    /// <summary>Where the document was e-mailed (personal data).</summary>
    public string? EmailedTo { get; private set; }

    public DateTimeOffset? EmailedAtUtc { get; private set; }

    /// <summary>Name of the PDF file offered to download: the number of the document.</summary>
    public string PdfFileName => $"{Number}.pdf";

    /// <summary>
    /// Whether the PDF can be produced: on paper at once; with TTN, once the document carries its TTN reference (printed
    /// on the PDF).
    /// </summary>
    public bool IsReadyForPdf => EInvoiceMode == EInvoiceMode.PaperTransitional || EInvoiceStatus == EInvoiceStatus.Transmitted;

    /// <summary>
    /// The path of the PDF of a document: <c>{year}/{number}-{id}.pdf</c>. The id keeps the path unique even where the
    /// numbering starts over on a storage that is kept (a recette database reset, test runs sharing one emulator).
    /// </summary>
    public static string PdfPathOf(int fiscalYear, string number, Guid documentId) =>
        string.Create(CultureInfo.InvariantCulture, $"{fiscalYear:D4}/{number}-{documentId:N}.pdf");

    /// <summary>TTN accepted the document: its unique reference is kept (once; the same reference again is a no-op).</summary>
    public Result RecordTransmission(string? reference, DateTimeOffset at)
    {
        if (EInvoiceMode != EInvoiceMode.Ttn)
        {
            return BillingErrors.EInvoiceNotTtn;
        }

        var clean = string.IsNullOrWhiteSpace(reference) ? null : reference.Trim();
        if (clean is null || clean.Length > TtnReferenceMaxLength)
        {
            return BillingErrors.EInvoiceReferenceInvalid;
        }

        if (EInvoiceStatus == EInvoiceStatus.Transmitted)
        {
            return string.Equals(TtnReference, clean, StringComparison.Ordinal) ? Result.Success() : BillingErrors.EInvoiceAlreadyTransmitted;
        }

        TtnReference = clean;
        TransmittedAtUtc = at.ToUniversalTime();
        LastTransmissionAttemptAtUtc = TransmittedAtUtc;
        TransmissionAttempts++;
        LastTransmissionError = null;
        EInvoiceStatus = EInvoiceStatus.Transmitted;
        Raise(new BillingDocumentTransmitted(Id, BillingType, Number, clean));
        return Result.Success();
    }

    /// <summary>A transmission failed: the attempt and its error are kept; the status shows the failure until it succeeds.</summary>
    public Result RecordTransmissionFailure(string? error, DateTimeOffset at)
    {
        if (EInvoiceMode != EInvoiceMode.Ttn)
        {
            return BillingErrors.EInvoiceNotTtn;
        }

        if (EInvoiceStatus == EInvoiceStatus.Transmitted)
        {
            return BillingErrors.EInvoiceAlreadyTransmitted;
        }

        var text = string.IsNullOrWhiteSpace(error) ? BillingErrors.EInvoiceTransmissionFailed.Code : error.Trim();
        LastTransmissionError = text.Length > ErrorMaxLength ? text[..ErrorMaxLength] : text;
        LastTransmissionAttemptAtUtc = at.ToUniversalTime();
        TransmissionAttempts++;
        EInvoiceStatus = EInvoiceStatus.Failed;
        Raise(new BillingDocumentTransmissionFailed(Id, BillingType, Number, TransmissionAttempts));
        return Result.Success();
    }

    /// <summary>The PDF of the document was stored (once: a stored PDF is never replaced).</summary>
    public Result AttachPdf(string? path, string? hash, long length, DateTimeOffset at)
    {
        if (PdfPath is not null)
        {
            return BillingErrors.PdfAlreadyStored;
        }

        if (string.IsNullOrWhiteSpace(path) || path.Length > PdfPathMaxLength || hash is not { Length: PdfHashLength } || length <= 0)
        {
            return BillingErrors.PdfInvalid;
        }

        PdfPath = path;
        PdfHash = hash;
        PdfLength = length;
        PdfStoredAtUtc = at.ToUniversalTime();
        Raise(new BillingDocumentPdfStored(Id, BillingType, Number, path));
        return Result.Success();
    }

    /// <summary>The document was queued for e-mail to <paramref name="to"/> with its PDF attached (once).</summary>
    public Result MarkEmailed(string? to, DateTimeOffset at)
    {
        if (EmailedAtUtc is not null)
        {
            return BillingErrors.AlreadyEmailed;
        }

        var address = string.IsNullOrWhiteSpace(to) ? null : to.Trim();
        if (address is null || address.Length > BuyerIdentity.EmailMaxLength || !address.Contains('@', StringComparison.Ordinal))
        {
            return BillingErrors.EmailInvalid;
        }

        EmailedTo = address;
        EmailedAtUtc = at.ToUniversalTime();
        Raise(new BillingDocumentEmailed(Id, BillingType, Number));
        return Result.Success();
    }

    /// <summary>Checks the mentions of a document: at most <see cref="MaxMentions"/>, none empty or too long.</summary>
    internal static Result<IReadOnlyList<string>> CleanMentions(IReadOnlyList<string>? mentions)
    {
        var clean = (mentions ?? []).Select(m => m?.Trim()).ToList();
        if (clean.Count > MaxMentions || clean.Any(m => string.IsNullOrEmpty(m) || m.Length > MentionMaxLength))
        {
            return BillingErrors.MentionInvalid;
        }

        return Result.Success<IReadOnlyList<string>>([.. clean.Select(m => m!)]);
    }

    /// <summary>Whether <paramref name="number"/> is a number of <paramref name="type"/> for a document issued at <paramref name="issuedAt"/>.</summary>
    protected static bool Fits(DocumentNumber number, DocumentType type, DateTimeOffset issuedAt) =>
        number is not null && number.Type == type && number.Year == TunisTime.DateOf(issuedAt).Year;
}
