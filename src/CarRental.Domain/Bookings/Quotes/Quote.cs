using CarRental.Domain.Common;

namespace CarRental.Domain.Bookings.Quotes;

/// <summary>
/// A frozen price (devis figé, PROMPT §6 invariant 5), aggregate of the Bookings module: either a standalone quote whose
/// price is locked for <see cref="LockDuration"/> (bot, funnel), or a version of the price of a rental file. It is never
/// updated after its creation: an amendment creates a new version of the file and keeps the previous ones.
/// </summary>
/// <remarks>
/// The aggregate exposes no mutator at all; its content (lines, totals, deposit, plan version, promotion outcome and the
/// JSON snapshots of the inputs) is checked once, at creation (<see cref="QuoteContent.Validate"/>).
/// </remarks>
public sealed class Quote : AggregateRoot
{
    public const int ReasonMaxLength = Booking.ReasonMaxLength;

    /// <summary>A standalone quote guarantees its price for 24 hours ("devis verrouillé 24 h", SPEC §5 and §11).</summary>
    public static readonly TimeSpan LockDuration = TimeSpan.FromHours(24);

    private readonly List<QuoteLine> _lines = [];

    private Quote()
    {
    }

    private Quote(Guid id, QuoteContent content, string code, QuoteStatus status, QuoteOrigin origin, DateTimeOffset at, string? createdBy)
        : base(id)
    {
        Code = code;
        Status = status;
        Origin = origin;
        AgencyId = content.AgencyId;
        CategoryId = content.CategoryId;
        Channel = content.Channel;
        PickupLocationId = content.PickupLocationId;
        ReturnLocationId = content.ReturnLocationId;
        PickupAt = content.PickupAt.ToUniversalTime();
        ReturnAt = content.ReturnAt.ToUniversalTime();
        RatePlanId = content.RatePlanId;
        RatePlanVersion = content.RatePlanVersion;
        Days = content.Days;
        TotalExclVat = content.TotalExclVat;
        TotalVat = content.TotalVat;
        TotalInclVat = content.TotalInclVat;
        RentalSubtotal = content.RentalSubtotal;
        Deposit = content.Deposit;
        IncludesFranchiseWaiver = content.IncludesFranchiseWaiver;
        PromoCodeOutcome = content.PromoCodeOutcome;
        PromoCode = content.PromoCode;
        AppliedPromotionsJson = content.AppliedPromotionsJson;
        InputsJson = content.InputsJson;
        DailyRatesJson = content.DailyRatesJson;
        CreatedAtUtc = at.ToUniversalTime();
        CreatedBy = createdBy;
        for (var position = 0; position < content.Lines.Count; position++)
        {
            _lines.Add(new QuoteLine(id, position + 1, content.Lines[position]));
        }
    }

    /// <summary>Public code (<see cref="QuoteCode"/>), unique.</summary>
    public string Code { get; private set; } = null!;

    public QuoteStatus Status { get; private set; }

    public QuoteOrigin Origin { get; private set; }

    /// <summary>The rental file this quote is a version of; <see langword="null"/> for a standalone (locked) quote.</summary>
    public Guid? BookingId { get; private set; }

    /// <summary>Version within the file, from 1; <see langword="null"/> for a standalone quote.</summary>
    public int? Version { get; private set; }

    /// <summary>The locked quote this version was copied from.</summary>
    public Guid? SourceQuoteId { get; private set; }

    /// <summary>Reason of the manual discount of this version.</summary>
    public string? ManualDiscountReason { get; private set; }

    public Guid AgencyId { get; private set; }

    public Guid CategoryId { get; private set; }

    public SalesChannel Channel { get; private set; }

    public Guid PickupLocationId { get; private set; }

    public Guid ReturnLocationId { get; private set; }

    public DateTimeOffset PickupAt { get; private set; }

    public DateTimeOffset ReturnAt { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    /// <summary>End of the price guarantee of a standalone quote; <see langword="null"/> for a version of a file.</summary>
    public DateTimeOffset? ExpiresAtUtc { get; private set; }

    public string? CreatedBy { get; private set; }

    public Guid RatePlanId { get; private set; }

    public int? RatePlanVersion { get; private set; }

    public int Days { get; private set; }

    public Money TotalExclVat { get; private set; }

    public Money TotalVat { get; private set; }

    public Money TotalInclVat { get; private set; }

    public Money RentalSubtotal { get; private set; }

    public Money Deposit { get; private set; }

    public bool IncludesFranchiseWaiver { get; private set; }

    public QuotePromoCodeOutcome PromoCodeOutcome { get; private set; }

    public string? PromoCode { get; private set; }

    public string AppliedPromotionsJson { get; private set; } = "[]";

    /// <summary>Snapshot of the inputs of the calculation (request, settings, plans, deposits) as JSON.</summary>
    public string InputsJson { get; private set; } = "{}";

    public string DailyRatesJson { get; private set; } = "[]";

    /// <summary>Lines in their order.</summary>
    public IReadOnlyList<QuoteLine> Lines => [.. _lines.OrderBy(line => line.Position)];

    /// <summary>A standalone quote whose price is guaranteed for <see cref="LockDuration"/> from <paramref name="at"/>.</summary>
    public static Result<Quote> CreateLocked(QuoteContent content, string code, string? createdBy, DateTimeOffset at)
    {
        var valid = Check(content, code);
        if (valid.IsFailure)
        {
            return valid.Error;
        }

        var quote = new Quote(NewId(), content, code, QuoteStatus.Locked, QuoteOrigin.Standalone, at, Clean(createdBy))
        {
            ExpiresAtUtc = at.ToUniversalTime() + LockDuration,
        };
        quote.Raise(new QuoteLocked(quote.Id, quote.AgencyId, quote.Code, quote.TotalInclVat, quote.ExpiresAtUtc.Value));
        return quote;
    }

    /// <summary>Version <paramref name="version"/> of the price of the file <paramref name="bookingId"/>.</summary>
    public static Result<Quote> CreateVersion(
        Guid bookingId,
        int version,
        QuoteOrigin origin,
        QuoteContent content,
        string code,
        string? createdBy,
        DateTimeOffset at,
        Guid? sourceQuoteId = null,
        string? manualDiscountReason = null)
    {
        if (bookingId == Guid.Empty)
        {
            throw new ArgumentException("A quote version belongs to a rental file.", nameof(bookingId));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(version, 1);
        if (origin == QuoteOrigin.Standalone || !Enum.IsDefined(origin))
        {
            throw new ArgumentOutOfRangeException(nameof(origin), origin, "A version of a file has a file origin.");
        }

        var valid = Check(content, code);
        if (valid.IsFailure)
        {
            return valid.Error;
        }

        var reason = Clean(manualDiscountReason);
        var hasDiscount = content.Lines.Any(line => line.Kind == QuoteLineKind.ManualDiscount);
        if (hasDiscount && reason is null)
        {
            return BookingErrors.ReasonRequired;
        }

        if (reason is { Length: > ReasonMaxLength })
        {
            return BookingErrors.ReasonTooLong;
        }

        var quote = new Quote(NewId(), content, code, QuoteStatus.BookingVersion, origin, at, Clean(createdBy))
        {
            BookingId = bookingId,
            Version = version,
            SourceQuoteId = sourceQuoteId,
            ManualDiscountReason = hasDiscount ? reason : null,
        };
        quote.Raise(new QuoteVersionCreated(quote.Id, bookingId, version, origin, quote.TotalInclVat));
        return quote;
    }

    /// <summary>The price guarantee of a standalone quote is over.</summary>
    public bool IsExpiredAt(DateTimeOffset now) => ExpiresAtUtc is { } expiry && expiry <= now;

    /// <summary>The reference a rental file keeps to this version.</summary>
    public BookingQuoteReference ToReference() =>
        Version is { } version
            ? new BookingQuoteReference(Id, version, TotalInclVat, Deposit)
            : throw new InvalidOperationException("A standalone quote is not a version of a rental file.");

    /// <summary>The frozen content, to build a later version from (extension, discount) or to copy a locked quote.</summary>
    public QuoteContent ToContent() => new(
        AgencyId,
        CategoryId,
        Channel,
        PickupLocationId,
        ReturnLocationId,
        PickupAt,
        ReturnAt,
        RatePlanId,
        RatePlanVersion,
        Days,
        [.. Lines.Select(line => line.ToData())],
        TotalExclVat,
        TotalVat,
        TotalInclVat,
        RentalSubtotal,
        Deposit,
        IncludesFranchiseWaiver,
        PromoCodeOutcome,
        PromoCode,
        AppliedPromotionsJson,
        InputsJson,
        DailyRatesJson);

    private static Result Check(QuoteContent content, string code)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (!QuoteCode.IsValid(code))
        {
            return BookingErrors.QuoteCodeInvalid;
        }

        return content.Validate();
    }

    private static string? Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length > AuditActor.UserNameMaxLength ? trimmed[..AuditActor.UserNameMaxLength] : trimmed;
    }
}

/// <summary>A line of a frozen quote (never changed).</summary>
public sealed class QuoteLine : Entity
{
    private QuoteLine()
    {
    }

    internal QuoteLine(Guid quoteId, int position, QuoteLineData data)
        : base(NewId())
    {
        QuoteId = quoteId;
        Position = position;
        Kind = data.Kind;
        Code = data.Code;
        LabelKey = data.LabelKey;
        LabelArguments = [.. data.LabelArguments];
        Quantity = data.Quantity;
        UnitPrice = data.UnitPrice;
        ExclVat = data.ExclVat;
        Vat = data.Vat;
        InclVat = data.InclVat;
        VatRate = data.VatRate;
    }

    public Guid QuoteId { get; private set; }

    /// <summary>Order of the line in the quote, from 1.</summary>
    public int Position { get; private set; }

    public QuoteLineKind Kind { get; private set; }

    public string Code { get; private set; } = null!;

    public string LabelKey { get; private set; } = null!;

    /// <summary>Invariant arguments of the label (numbers with a dot, names).</summary>
    public IReadOnlyList<string> LabelArguments { get; private set; } = [];

    public int Quantity { get; private set; }

    /// <summary>Unit price, VAT included.</summary>
    public Money UnitPrice { get; private set; }

    public Money ExclVat { get; private set; }

    public Money Vat { get; private set; }

    public Money InclVat { get; private set; }

    public decimal VatRate { get; private set; }

    public QuoteLineData ToData() => new(Kind, Code, LabelKey, LabelArguments, Quantity, UnitPrice, ExclVat, Vat, InclVat, VatRate);
}

/// <summary>A standalone quote was created; its price is guaranteed until <c>ExpiresAtUtc</c>.</summary>
public sealed record QuoteLocked(Guid QuoteId, Guid AgencyId, string Code, Money TotalInclVat, DateTimeOffset ExpiresAtUtc) : IDomainEvent;

/// <summary>A new version of the price of a rental file was frozen.</summary>
public sealed record QuoteVersionCreated(Guid QuoteId, Guid BookingId, int Version, QuoteOrigin Origin, Money TotalInclVat) : IDomainEvent;
