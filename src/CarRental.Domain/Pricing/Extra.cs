using CarRental.Domain.Common;

namespace CarRental.Domain.Pricing;

/// <summary>
/// An extra of the rate plan catalogue (option): chosen by the customer (additional driver, child seat, franchise
/// waiver…) or added automatically (young driver, delivery to a location, after-hours, one-way). Prices are VAT included.
/// </summary>
public sealed class Extra : Entity
{
    public const int CodeMinLength = 2;
    public const int CodeMaxLength = 32;
    public const int NameMaxLength = 80;

    private Extra()
    {
    }

    private Extra(Guid id, ExtraDefinition definition, int position)
        : base(id) => Apply(definition, position);

    /// <summary>Stable upper-case code (e.g. <c>CHILD_SEAT</c>), unique in the plan; the booking funnel selects extras by code.</summary>
    public string Code { get; private set; } = null!;

    /// <summary>Name shown to French-speaking users (neutral language).</summary>
    public string Name { get; private set; } = null!;

    /// <summary>English name, or <see langword="null"/> to reuse <see cref="Name"/>.</summary>
    public string? NameEn { get; private set; }

    public ExtraKind Kind { get; private set; }

    public ExtraPricingMode Mode { get; private set; }

    /// <summary>Unit price TTC: per day for <see cref="ExtraPricingMode.PerDay"/>, per rental otherwise.</summary>
    public Money Price { get; private set; }

    /// <summary>Maximum charged per unit for the whole rental (per-day extras only), e.g. 50.000 for a child seat.</summary>
    public Money? CapPerUnit { get; private set; }

    /// <summary>Location (Referential module) triggering a <see cref="ExtraKind.LocationDelivery"/> extra.</summary>
    public Guid? LocationId { get; private set; }

    public bool IsActive { get; private set; }

    /// <summary>Order of the extra in the catalogue (and in quotes).</summary>
    public int Position { get; private set; }

    public bool IsSelectable => Kind.IsSelectable();

    /// <summary>Normalizes a code the way extras store it (trimmed, upper case).</summary>
    public static string NormalizeCode(string? code) => (code ?? string.Empty).Trim().ToUpperInvariant();

    internal static bool IsValid(ExtraDefinition definition)
    {
        var code = NormalizeCode(definition.Code);
        var name = (definition.Name ?? string.Empty).Trim();
        var nameEn = definition.NameEn?.Trim() ?? string.Empty;
        if (code.Length is < CodeMinLength or > CodeMaxLength
            || !char.IsAsciiLetterUpper(code[0])
            || !code.All(c => char.IsAsciiLetterUpper(c) || char.IsAsciiDigit(c) || c == '_')
            || name.Length is 0 or > NameMaxLength
            || nameEn.Length > NameMaxLength
            || !Enum.IsDefined(definition.Kind)
            || !Enum.IsDefined(definition.Mode)
            || !PricingValues.IsNonNegativeTnd(definition.Price))
        {
            return false;
        }

        if (definition.CapPerUnit is { } cap && (definition.Mode != ExtraPricingMode.PerDay || !PricingValues.IsPositiveTnd(cap)))
        {
            return false;
        }

        var isDelivery = definition.Kind == ExtraKind.LocationDelivery;
        var hasLocation = definition.LocationId is { } locationId && locationId != Guid.Empty;
        if (isDelivery != hasLocation || (!isDelivery && definition.LocationId is not null))
        {
            return false;
        }

        var flatOnly = definition.Kind is ExtraKind.LocationDelivery or ExtraKind.AfterHours or ExtraKind.OneWay;
        return !flatOnly || definition.Mode == ExtraPricingMode.PerRental;
    }

    internal static Extra Create(ExtraDefinition definition, int position) => new(NewId(), definition, position);

    internal Extra CopyForNewPlan() => new(NewId(), ToDefinition() with { Id = null }, Position);

    internal ExtraDefinition ToDefinition() => new(Code, Name, Kind, Mode, Price, CapPerUnit, LocationId, IsActive, NameEn, Id);

    internal void Apply(ExtraDefinition definition, int position)
    {
        Code = NormalizeCode(definition.Code);
        Name = definition.Name.Trim();
        NameEn = string.IsNullOrWhiteSpace(definition.NameEn) ? null : definition.NameEn.Trim();
        Kind = definition.Kind;
        Mode = definition.Mode;
        Price = definition.Price;
        CapPerUnit = definition.CapPerUnit;
        LocationId = definition.LocationId;
        IsActive = definition.IsActive;
        Position = position;
    }
}

/// <summary>Content of an extra. <paramref name="Id"/> identifies an existing extra to update.</summary>
public sealed record ExtraDefinition(
    string Code,
    string Name,
    ExtraKind Kind,
    ExtraPricingMode Mode,
    Money Price,
    Money? CapPerUnit = null,
    Guid? LocationId = null,
    bool IsActive = true,
    string? NameEn = null,
    Guid? Id = null);
