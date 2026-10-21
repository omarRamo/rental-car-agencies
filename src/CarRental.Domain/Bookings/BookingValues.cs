using System.ComponentModel.DataAnnotations.Schema;
using System.Globalization;
using CarRental.Domain.Common;

namespace CarRental.Domain.Bookings;

/// <summary>
/// Who performs a command on a rental file and through which sales channel it came: a user of the back office (channel
/// <see langword="null"/>), a customer through the public site or WhatsApp link, a channel, or the platform itself.
/// </summary>
public sealed record BookingActor
{
    public const int NameMaxLength = AuditActor.UserNameMaxLength;

    /// <summary>Name of the platform actor when no more specific system name is given.</summary>
    public const string SystemName = "system";

    public BookingActor(Guid? userId, string? name, SalesChannel? channel = null)
    {
        var trimmed = string.IsNullOrWhiteSpace(name) ? SystemName : name.Trim();
        UserId = userId;
        Name = trimmed.Length > NameMaxLength ? trimmed[..NameMaxLength] : trimmed;
        Channel = channel;
    }

    /// <summary>The user, or <see langword="null"/> for the platform and anonymous customers.</summary>
    public Guid? UserId { get; }

    /// <summary>User name, customer label or system actor (e.g. <c>system:bookings-expire</c>).</summary>
    public string Name { get; }

    /// <summary>The sales channel the command came through, <see langword="null"/> from the back office or the platform.</summary>
    public SalesChannel? Channel { get; }

    public static BookingActor System(string? name = null) => new(null, name ?? SystemName);
}

/// <summary>Where a request came from (SPEC §5 "src et les paramètres UTM attribuent la vente à son canal").</summary>
// [ComplexType] (BCL annotation): a value stored in the columns of the file, never a table of its own.
[ComplexType]
public sealed record BookingAttribution
{
    public const int FieldMaxLength = 100;

    private BookingAttribution(string? source, string? utmSource, string? utmMedium, string? utmCampaign, string? utmTerm, string? utmContent)
    {
        Source = source;
        UtmSource = utmSource;
        UtmMedium = utmMedium;
        UtmCampaign = utmCampaign;
        UtmTerm = utmTerm;
        UtmContent = utmContent;
    }

    /// <summary>The <c>src</c> parameter of a deep link (e.g. <c>whatsapp</c>), or the referrer chosen by an agent.</summary>
    public string? Source { get; private init; }

    public string? UtmSource { get; private init; }

    public string? UtmMedium { get; private init; }

    public string? UtmCampaign { get; private init; }

    public string? UtmTerm { get; private init; }

    public string? UtmContent { get; private init; }

    /// <summary>No attribution (an agent's booking without a declared source).</summary>
    public static BookingAttribution None { get; } = new(null, null, null, null, null, null);

    public bool IsEmpty => Source is null && UtmSource is null && UtmMedium is null && UtmCampaign is null && UtmTerm is null && UtmContent is null;

    /// <summary>Trims the fields; <see cref="None"/> when every field is empty.</summary>
    public static Result<BookingAttribution> Create(
        string? source,
        string? utmSource = null,
        string? utmMedium = null,
        string? utmCampaign = null,
        string? utmTerm = null,
        string? utmContent = null)
    {
        string?[] fields = [Clean(source), Clean(utmSource), Clean(utmMedium), Clean(utmCampaign), Clean(utmTerm), Clean(utmContent)];
        if (fields.Any(field => field is { Length: > FieldMaxLength }))
        {
            return BookingErrors.AttributionInvalid;
        }

        return fields.All(field => field is null)
            ? None
            : new BookingAttribution(fields[0], fields[1], fields[2], fields[3], fields[4], fields[5]);
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>
/// The traced decision of a manager to let a vehicle whose mandatory documents expire before the return carry this
/// rental file (PROMPT §6 invariant 3). It applies to <see cref="VehicleId"/> only and never waives an overlap.
/// </summary>
[ComplexType]
public sealed record ComplianceWaiver
{
    public ComplianceWaiver(Guid vehicleId, Guid? byUserId, string byName, DateTimeOffset atUtc, string reason)
    {
        VehicleId = vehicleId;
        ByUserId = byUserId;
        ByName = byName;
        AtUtc = atUtc.ToUniversalTime();
        Reason = reason;
    }

    public Guid VehicleId { get; private init; }

    public Guid? ByUserId { get; private init; }

    public string ByName { get; private init; }

    public DateTimeOffset AtUtc { get; private init; }

    public string Reason { get; private init; }
}

/// <summary>A selectable extra chosen for the rental (code of the rate plan catalogue) and its quantity.</summary>
// [NotMapped] (BCL annotation): a value of the domain, stored by the file in its own text form, never an entity.
[NotMapped]
public sealed record ExtraChoice
{
    public const int CodeMaxLength = 32;
    public const int MaximumQuantity = 10;

    private ExtraChoice(string code, int quantity)
    {
        Code = code;
        Quantity = quantity;
    }

    /// <summary>Normalized code (upper case letters, digits and underscores).</summary>
    public string Code { get; }

    public int Quantity { get; }

    public static Result<ExtraChoice> Create(string? code, int quantity)
    {
        var normalized = (code ?? string.Empty).Trim().ToUpperInvariant();
        if (normalized.Length is < 2 or > CodeMaxLength
            || !char.IsAsciiLetterUpper(normalized[0])
            || !normalized.All(c => char.IsAsciiLetterUpper(c) || char.IsAsciiDigit(c) || c == '_')
            || quantity is < 1 or > MaximumQuantity)
        {
            return BookingErrors.ExtrasInvalid;
        }

        return new ExtraChoice(normalized, quantity);
    }

    /// <summary>Merges the quantities of a same code, orders by code; fails on an invalid entry or a total above the maximum.</summary>
    public static Result<IReadOnlyList<ExtraChoice>> Normalize(IEnumerable<ExtraChoice>? choices)
    {
        var merged = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var choice in choices ?? [])
        {
            ArgumentNullException.ThrowIfNull(choice, nameof(choices));
            merged[choice.Code] = merged.GetValueOrDefault(choice.Code) + choice.Quantity;
        }

        if (merged.Values.Any(quantity => quantity > MaximumQuantity))
        {
            return BookingErrors.ExtrasInvalid;
        }

        return Result.Success<IReadOnlyList<ExtraChoice>>([.. merged.Select(pair => new ExtraChoice(pair.Key, pair.Value))]);
    }

    /// <summary>Stored form: <c>CODE:QTY;CODE:QTY</c> in code order, empty when nothing is chosen.</summary>
    public static string Format(IEnumerable<ExtraChoice> choices) =>
        string.Join(';', choices.Select(choice => string.Create(CultureInfo.InvariantCulture, $"{choice.Code}:{choice.Quantity}")));

    /// <summary>Reads the stored form written by <see cref="Format"/>.</summary>
    public static IReadOnlyList<ExtraChoice> Parse(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored))
        {
            return [];
        }

        var choices = new List<ExtraChoice>();
        foreach (var part in stored.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = part.LastIndexOf(':');
            if (separator > 0
                && int.TryParse(part.AsSpan(separator + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var quantity)
                && Create(part[..separator], quantity) is { IsSuccess: true } choice)
            {
                choices.Add(choice.Value);
            }
        }

        return choices;
    }
}

/// <summary>The persons and payer of a rental file: drivers are persons, the payer a customer (Customers module ids).</summary>
[NotMapped]
public sealed record BookingParties(Guid? MainDriverId, Guid? SecondaryDriverId, Guid? PayerCustomerId)
{
    public static BookingParties None { get; } = new(null, null, null);

    /// <summary>Main and second drivers, without the missing ones.</summary>
    public IReadOnlyList<Guid> Drivers => [.. new[] { MainDriverId, SecondaryDriverId }.OfType<Guid>()];

    /// <summary>Ids are never empty, a second driver needs a main driver and differs from it; <paramref name="complete"/>
    /// requires the main driver and the payer (every status but <c>Draft</c>).</summary>
    internal Result Validate(bool complete)
    {
        if (MainDriverId == Guid.Empty || SecondaryDriverId == Guid.Empty || (SecondaryDriverId is not null && MainDriverId is null))
        {
            return BookingErrors.MainDriverRequired;
        }

        if (PayerCustomerId == Guid.Empty)
        {
            return BookingErrors.PayerRequired;
        }

        if (SecondaryDriverId is not null && SecondaryDriverId == MainDriverId)
        {
            return BookingErrors.DriversIdentical;
        }

        if (complete && MainDriverId is null)
        {
            return BookingErrors.MainDriverRequired;
        }

        return complete && PayerCustomerId is null ? BookingErrors.PayerRequired : Result.Success();
    }
}

/// <summary>Predefined reason of an information request (motif prédéfini), completed by a message to the customer.</summary>
public enum InformationRequestReason
{
    MissingDocuments = 1,
    UnreadableDocuments = 2,
    NameMismatch = 3,
    DocumentExpiresBeforeReturn = 4,
    LicenseSeniority = 5,
    InternationalPermitRequired = 6,
    Other = 99,
}

/// <summary>Reference to the frozen quote version a rental file is priced by, with its totals.</summary>
public sealed record BookingQuoteReference(Guid QuoteId, int Version, Money TotalInclVat, Money Deposit);
