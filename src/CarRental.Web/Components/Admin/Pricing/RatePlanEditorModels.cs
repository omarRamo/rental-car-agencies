using System.Globalization;
using CarRental.Domain.Common;
using CarRental.Domain.Pricing;

namespace CarRental.Web.Components.Admin.Pricing;

/// <summary>Conversions between the editor fields (text dates, decimals) and the definitions of the domain.</summary>
internal static class EditorValues
{
    public const string DateFormat = "yyyy-MM-dd";

    public static string? Text(DateOnly? date) => date?.ToString(DateFormat, CultureInfo.InvariantCulture);

    public static DateOnly? ParseDate(string? text) =>
        DateOnly.TryParseExact(text?.Trim(), DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;

    /// <summary>An amount in TND, refused when it has more than three decimals (no silent rounding of what was typed).</summary>
    public static Result<Money> Amount(decimal value) =>
        Money.FromExact(value, Currency.Tnd) is { IsSuccess: true } money ? money.Value : PricingErrors.AmountInvalid;
}

/// <summary>A season being edited.</summary>
internal sealed class SeasonRow
{
    public Guid? Id { get; set; }

    public SeasonLevel Level { get; set; } = SeasonLevel.Mid;

    public string? Start { get; set; }

    public string? End { get; set; }

    public int MinimumDays { get; set; }

    public static SeasonRow From(SeasonDefinition season) => new()
    {
        Id = season.Id,
        Level = season.Level,
        Start = EditorValues.Text(season.StartDate),
        End = EditorValues.Text(season.EndDate),
        MinimumDays = season.MinimumDays,
    };

    public SeasonDefinition? ToDefinition() =>
        EditorValues.ParseDate(Start) is { } start && EditorValues.ParseDate(End) is { } end
            ? new SeasonDefinition(Level, start, end, MinimumDays, Id)
            : null;
}

/// <summary>A priority event being edited.</summary>
internal sealed class EventRow
{
    public Guid? Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? Start { get; set; }

    public string? End { get; set; }

    public int Priority { get; set; } = 1;

    public int MinimumDays { get; set; }

    public static EventRow From(PriorityEventDefinition priorityEvent) => new()
    {
        Id = priorityEvent.Id,
        Name = priorityEvent.Name,
        Start = EditorValues.Text(priorityEvent.StartDate),
        End = EditorValues.Text(priorityEvent.EndDate),
        Priority = priorityEvent.Priority,
        MinimumDays = priorityEvent.MinimumDays,
    };

    public PriorityEventDefinition? ToDefinition() =>
        EditorValues.ParseDate(Start) is { } start && EditorValues.ParseDate(End) is { } end
            ? new PriorityEventDefinition(Name, start, end, Priority, MinimumDays, Id)
            : null;
}

/// <summary>A duration tier being edited (its last day is derived from the next tier).</summary>
internal sealed class TierRow
{
    public int MinimumDays { get; set; } = 1;

    public decimal DiscountPercent { get; set; }
}

/// <summary>An extra being edited.</summary>
internal sealed class ExtraRow
{
    public Guid? Id { get; set; }

    public string Code { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string? NameEn { get; set; }

    public ExtraKind Kind { get; set; } = ExtraKind.Optional;

    public ExtraPricingMode Mode { get; set; } = ExtraPricingMode.PerDay;

    public decimal Price { get; set; }

    public decimal? CapPerUnit { get; set; }

    public Guid? LocationId { get; set; }

    public bool IsActive { get; set; } = true;

    public static ExtraRow From(ExtraDefinition extra) => new()
    {
        Id = extra.Id,
        Code = extra.Code,
        Name = extra.Name,
        NameEn = extra.NameEn,
        Kind = extra.Kind,
        Mode = extra.Mode,
        Price = extra.Price.Amount,
        CapPerUnit = extra.CapPerUnit?.Amount,
        LocationId = extra.LocationId,
        IsActive = extra.IsActive,
    };

    public Result<ExtraDefinition> ToDefinition()
    {
        var price = EditorValues.Amount(Price);
        var cap = CapPerUnit is { } capAmount ? EditorValues.Amount(capAmount) : null;
        if (price.IsFailure || cap is { IsFailure: true })
        {
            return PricingErrors.AmountInvalid;
        }

        return new ExtraDefinition(
            Code,
            Name,
            Kind,
            Mode,
            price.Value,
            cap?.Value,
            Kind == ExtraKind.LocationDelivery ? LocationId : null,
            IsActive,
            NameEn,
            Id);
    }
}

/// <summary>A promotion being edited.</summary>
internal sealed class PromotionRow
{
    public Guid? Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public PromotionKind Kind { get; set; } = PromotionKind.Code;

    public string? Code { get; set; }

    public int? MinimumDays { get; set; }

    public PromotionDiscountType DiscountType { get; set; } = PromotionDiscountType.Percentage;

    public decimal? DiscountValue { get; set; }

    public string? BookableFrom { get; set; }

    public string? BookableUntil { get; set; }

    public string? PickupFrom { get; set; }

    public string? PickupUntil { get; set; }

    public IReadOnlyCollection<Guid> CategoryIds { get; set; } = [];

    public IReadOnlyCollection<SalesChannel> Channels { get; set; } = [];

    public bool IsCombinable { get; set; }

    public bool IsActive { get; set; } = true;

    public static PromotionRow From(PromotionDefinition promotion) => new()
    {
        Id = promotion.Id,
        Name = promotion.Name,
        Kind = promotion.Kind,
        Code = promotion.Code,
        MinimumDays = promotion.MinimumDays,
        DiscountType = promotion.DiscountType,
        DiscountValue = promotion.DiscountType == PromotionDiscountType.Percentage ? promotion.DiscountPercent : promotion.DiscountAmount?.Amount,
        BookableFrom = EditorValues.Text(promotion.BookableFrom),
        BookableUntil = EditorValues.Text(promotion.BookableUntil),
        PickupFrom = EditorValues.Text(promotion.PickupFrom),
        PickupUntil = EditorValues.Text(promotion.PickupUntil),
        CategoryIds = promotion.CategoryIds ?? [],
        Channels = promotion.Channels ?? [],
        IsCombinable = promotion.IsCombinable,
        IsActive = promotion.IsActive,
    };

    public Result<PromotionDefinition> ToDefinition()
    {
        Money? amount = null;
        if (DiscountType == PromotionDiscountType.FixedAmount && DiscountValue is { } value)
        {
            var parsed = EditorValues.Amount(value);
            if (parsed.IsFailure)
            {
                return parsed.Error;
            }

            amount = parsed.Value;
        }

        return new PromotionDefinition(
            Name,
            Kind,
            DiscountType,
            DiscountType == PromotionDiscountType.Percentage ? DiscountValue : null,
            amount,
            Kind == PromotionKind.Code ? Code : null,
            Kind == PromotionKind.Code ? null : MinimumDays,
            EditorValues.ParseDate(BookableFrom),
            EditorValues.ParseDate(BookableUntil),
            EditorValues.ParseDate(PickupFrom),
            EditorValues.ParseDate(PickupUntil),
            [.. CategoryIds],
            [.. Channels],
            IsCombinable,
            IsActive,
            Id);
    }
}

/// <summary>The rule of one channel being edited.</summary>
internal sealed class ChannelRuleRow
{
    public SalesChannel Channel { get; init; }

    public ChannelPricingMode Mode { get; set; } = ChannelPricingMode.Parity;

    public decimal? MarkupPercent { get; set; }

    public ChannelRuleDefinition ToDefinition() =>
        new(Channel, Mode, Mode == ChannelPricingMode.Markup ? MarkupPercent : null);
}
