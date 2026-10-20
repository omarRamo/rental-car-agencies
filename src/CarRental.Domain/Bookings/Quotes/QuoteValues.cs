using System.Security.Cryptography;
using CarRental.Domain.Common;

namespace CarRental.Domain.Bookings.Quotes;

/// <summary>Kind of a frozen quote line: the pricing engine kinds (same values) plus the lines of the Bookings module.</summary>
public enum QuoteLineKind
{
    Rental = 1,
    DurationDiscount = 2,
    Promotion = 3,
    GuaranteedModel = 4,
    Extra = 5,
    FranchiseWaiver = 6,
    YoungDriver = 7,
    Delivery = 8,
    AfterHours = 9,
    OneWay = 10,

    /// <summary>Daily contribution (2.000 TND HT per day and vehicle), also for the days added by an extension.</summary>
    DailyContribution = 11,

    /// <summary>Rental days added by an extension, valued at the current plan (SPEC §6).</summary>
    Extension = 20,

    /// <summary>A negative line granted by an agent, with a reason.</summary>
    ManualDiscount = 21,
}

/// <summary>Outcome of the promotion code of the priced request (same values as the pricing contract).</summary>
public enum QuotePromoCodeOutcome
{
    None = 0,
    Applied = 1,
    Unknown = 2,
    NotApplicable = 3,
    NotRetained = 4,
}

/// <summary>What a quote is. A quote never changes after its creation.</summary>
public enum QuoteStatus
{
    /// <summary>A standalone price guaranteed until its expiry (WhatsApp bot, public funnel, phone quote); never attached.</summary>
    Locked = 1,

    /// <summary>A version of the price of a rental file (booking id and version set).</summary>
    BookingVersion = 2,
}

/// <summary>How a quote version was produced.</summary>
public enum QuoteOrigin
{
    /// <summary>A locked quote, priced on request.</summary>
    Standalone = 1,

    /// <summary>First version of a file, priced at its creation.</summary>
    Initial = 2,

    /// <summary>First version of a file copied from a locked quote (the guaranteed price).</summary>
    LockedQuote = 3,

    /// <summary>An amendment priced again at the current plan (dates before pick-up, category, locations, extras, drivers).</summary>
    Requote = 4,

    /// <summary>An extension: the previous lines plus the added days valued at the current plan.</summary>
    Extension = 5,

    /// <summary>The previous lines plus a manual discount.</summary>
    ManualDiscount = 6,
}

/// <summary>Resource keys of the quote lines added by the Bookings module (translated by the web host).</summary>
public static class BookingQuoteLabels
{
    /// <summary>{0} days added by the extension.</summary>
    public const string Extension = "BookingQuote.Line.Extension";

    /// <summary>{0} days added by the extension, {1} contribution per day excluding VAT (amount).</summary>
    public const string ExtensionContribution = "BookingQuote.Line.ExtensionContribution";

    /// <summary>No argument: the reason is on the quote version.</summary>
    public const string ManualDiscount = "BookingQuote.Line.ManualDiscount";

    public static IReadOnlyList<string> All { get; } = [Extension, ExtensionContribution, ManualDiscount];

    /// <summary>True for the keys of this module (the others are pricing keys).</summary>
    public static bool IsBookingLabel(string key) => key.StartsWith("BookingQuote.", StringComparison.Ordinal);
}

/// <summary>Codes of the lines added by the Bookings module (the engine lines keep their pricing codes).</summary>
public static class BookingQuoteLineCodes
{
    public const string Extension = "EXTENSION";
    public const string DailyContribution = "DAILY_CONTRIBUTION";
    public const string ManualDiscount = "MANUAL_DISCOUNT";
}

/// <summary>
/// Public code of a quote: six characters of an unambiguous alphabet (no 0/O, 1/I/L), e.g. <c>Q7F3K2</c>, used by the
/// deep links (<c>devis=Q7F3K2</c>) and the bot's locked quotes. Random: 31⁶ ≈ 887 million codes.
/// </summary>
public static class QuoteCode
{
    public const string Alphabet = "23456789ABCDEFGHJKMNPQRSTUVWXYZ";
    public const int Length = 6;

    /// <summary>A new random code (cryptographic generator: codes are not guessable in sequence).</summary>
    public static string Generate()
    {
        Span<char> code = stackalloc char[Length];
        for (var i = 0; i < Length; i++)
        {
            code[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        }

        return new string(code);
    }

    /// <summary>Normalizes a typed code (trimmed, upper case) and checks it.</summary>
    public static Result<string> Normalize(string? code)
    {
        var normalized = (code ?? string.Empty).Trim().ToUpperInvariant();
        return IsValid(normalized) ? normalized : BookingErrors.QuoteCodeInvalid;
    }

    public static bool IsValid(string? code) =>
        code is { Length: Length } && code.All(c => Alphabet.Contains(c, StringComparison.Ordinal));
}

/// <summary>
/// A frozen line: kind, code, label key and invariant arguments, quantity, unit price TTC, HT, VAT, TTC and rate. Two
/// lines are equal when every value is (label arguments compared item by item).
/// </summary>
public sealed record QuoteLineData(
    QuoteLineKind Kind,
    string Code,
    string LabelKey,
    IReadOnlyList<string> LabelArguments,
    int Quantity,
    Money UnitPrice,
    Money ExclVat,
    Money Vat,
    Money InclVat,
    decimal VatRate)
{
    public bool Equals(QuoteLineData? other) =>
        other is not null
        && Kind == other.Kind
        && string.Equals(Code, other.Code, StringComparison.Ordinal)
        && string.Equals(LabelKey, other.LabelKey, StringComparison.Ordinal)
        && LabelArguments.SequenceEqual(other.LabelArguments, StringComparer.Ordinal)
        && Quantity == other.Quantity
        && UnitPrice == other.UnitPrice
        && ExclVat == other.ExclVat
        && Vat == other.Vat
        && InclVat == other.InclVat
        && VatRate == other.VatRate;

    public override int GetHashCode() => HashCode.Combine(Kind, Code, LabelKey, Quantity, InclVat, VatRate);
}

/// <summary>
/// The full frozen content of a quote: what was priced (agency, category, channel, locations, planned instants), with
/// which plan, its lines and totals, the deposit (outside the total), the promotion outcome and the JSON snapshots of the
/// applied promotions, the inputs and the daily rates (written by the application, opaque here).
/// </summary>
public sealed record QuoteContent(
    Guid AgencyId,
    Guid CategoryId,
    SalesChannel Channel,
    Guid PickupLocationId,
    Guid ReturnLocationId,
    DateTimeOffset PickupAt,
    DateTimeOffset ReturnAt,
    Guid RatePlanId,
    int? RatePlanVersion,
    int Days,
    IReadOnlyList<QuoteLineData> Lines,
    Money TotalExclVat,
    Money TotalVat,
    Money TotalInclVat,
    Money RentalSubtotal,
    Money Deposit,
    bool IncludesFranchiseWaiver,
    QuotePromoCodeOutcome PromoCodeOutcome,
    string? PromoCode,
    string AppliedPromotionsJson,
    string InputsJson,
    string DailyRatesJson)
{
    public const int LineCodeMaxLength = 32;
    public const int LabelKeyMaxLength = 100;
    public const int LabelArgumentMaxLength = 200;

    /// <summary>The same content with the totals recomputed from <paramref name="lines"/>.</summary>
    public QuoteContent WithLines(IReadOnlyList<QuoteLineData> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        return this with
        {
            Lines = lines,
            TotalExclVat = lines.Select(l => l.ExclVat).Sum(),
            TotalVat = lines.Select(l => l.Vat).Sum(),
            TotalInclVat = lines.Select(l => l.InclVat).Sum(),
        };
    }

    /// <summary>
    /// The invariants of a frozen quote: ids and period, lines HT + VAT = TTC, totals equal to the sums of the lines, a
    /// non-negative total and deposit, amounts in TND millimes, bounded texts.
    /// </summary>
    public Result Validate()
    {
        if (AgencyId == Guid.Empty || CategoryId == Guid.Empty || PickupLocationId == Guid.Empty || ReturnLocationId == Guid.Empty
            || RatePlanId == Guid.Empty || !Enum.IsDefined(Channel) || ReturnAt <= PickupAt || Days < 1
            || !Enum.IsDefined(PromoCodeOutcome) || Lines is null || Lines.Count == 0
            || AppliedPromotionsJson is null || InputsJson is null || DailyRatesJson is null)
        {
            return BookingErrors.QuoteInvalid;
        }

        foreach (var line in Lines)
        {
            if (line is null
                || !Enum.IsDefined(line.Kind)
                || string.IsNullOrWhiteSpace(line.Code) || line.Code.Length > LineCodeMaxLength
                || string.IsNullOrWhiteSpace(line.LabelKey) || line.LabelKey.Length > LabelKeyMaxLength
                || line.LabelArguments is null || line.LabelArguments.Any(a => a is null || a.Length > LabelArgumentMaxLength)
                || line.Quantity < 0
                || line.VatRate is < 0m or > 1m
                || line.ExclVat + line.Vat != line.InclVat
                || !IsTnd(line.UnitPrice) || !IsTnd(line.ExclVat) || !IsTnd(line.Vat) || !IsTnd(line.InclVat))
            {
                return BookingErrors.QuoteInvalid;
            }
        }

        if (Lines.Select(l => l.InclVat).Sum() != TotalInclVat
            || Lines.Select(l => l.ExclVat).Sum() != TotalExclVat
            || Lines.Select(l => l.Vat).Sum() != TotalVat
            || TotalExclVat + TotalVat != TotalInclVat
            || !IsTnd(RentalSubtotal) || !IsTnd(Deposit) || Deposit.IsNegative)
        {
            return BookingErrors.QuoteInvalid;
        }

        return TotalInclVat.IsNegative ? BookingErrors.NegativeTotal : Result.Success();
    }

    private static bool IsTnd(Money amount) => amount.Currency == Currency.Tnd;
}
