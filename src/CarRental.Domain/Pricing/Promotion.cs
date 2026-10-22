using CarRental.Domain.Common;

namespace CarRental.Domain.Pricing;

/// <summary>
/// A promotion of the rate plan: a code, an early booking or a long stay discount, as a percentage or a fixed amount
/// of the rental subtotal after the duration tier. Optional filters: booking dates, pick-up dates, categories and
/// channels (empty = all). Non-combinable by default: the engine applies the best non-combinable promotion plus every
/// combinable one, never below zero.
/// </summary>
public sealed class Promotion : Entity, IAgencyOwned
{
    public const int NameMaxLength = 80;
    public const int CodeMinLength = 3;
    public const int CodeMaxLength = 32;

    private Promotion()
    {
    }

    private Promotion(Guid id, RatePlan plan, PromotionDefinition definition, int position)
        : base(id)
    {
        RatePlanId = plan.Id;
        AgencyId = plan.AgencyId;
        Apply(definition, position);
    }

    public Guid RatePlanId { get; private set; }

    /// <summary>The agency of the plan (ADR-0067).</summary>
    public Guid AgencyId { get; private set; }

    /// <summary>Back-office label (e.g. "Bienvenue 10 %").</summary>
    public string Name { get; private set; } = null!;

    public PromotionKind Kind { get; private set; }

    /// <summary>Upper-case code of a <see cref="PromotionKind.Code"/> promotion, <see langword="null"/> otherwise.</summary>
    public string? Code { get; private set; }

    /// <summary>Days before pick-up (early booking) or rental days (long stay); <see langword="null"/> for a code.</summary>
    public int? MinimumDays { get; private set; }

    public PromotionDiscountType DiscountType { get; private set; }

    /// <summary>Percentage of a <see cref="PromotionDiscountType.Percentage"/> promotion (above 0, up to 100).</summary>
    public decimal? DiscountPercent { get; private set; }

    /// <summary>Amount TTC of a <see cref="PromotionDiscountType.FixedAmount"/> promotion.</summary>
    public Money? DiscountAmount { get; private set; }

    /// <summary>First booking date (Tunis) on which the promotion is offered, included.</summary>
    public DateOnly? BookableFrom { get; private set; }

    /// <summary>Last booking date (Tunis) on which the promotion is offered, included.</summary>
    public DateOnly? BookableUntil { get; private set; }

    /// <summary>First pick-up date (Tunis) covered, included.</summary>
    public DateOnly? PickupFrom { get; private set; }

    /// <summary>Last pick-up date (Tunis) covered, included.</summary>
    public DateOnly? PickupUntil { get; private set; }

    /// <summary>Categories the promotion applies to; empty for every category.</summary>
    public IReadOnlyList<Guid> CategoryIds { get; private set; } = [];

    /// <summary>Channels the promotion applies to; empty for every channel.</summary>
    public IReadOnlyList<SalesChannel> Channels { get; private set; } = [];

    public bool IsCombinable { get; private set; }

    public bool IsActive { get; private set; }

    public int Position { get; private set; }

    public static string NormalizeCode(string? code) => (code ?? string.Empty).Trim().ToUpperInvariant();

    internal static bool IsValid(PromotionDefinition definition)
    {
        var name = (definition.Name ?? string.Empty).Trim();
        if (name.Length is 0 or > NameMaxLength || !Enum.IsDefined(definition.Kind) || !Enum.IsDefined(definition.DiscountType))
        {
            return false;
        }

        if (definition.Kind == PromotionKind.Code)
        {
            var code = NormalizeCode(definition.Code);
            if (code.Length is < CodeMinLength or > CodeMaxLength
                || !code.All(c => char.IsAsciiLetterUpper(c) || char.IsAsciiDigit(c) || c is '_' or '-')
                || definition.MinimumDays is not null)
            {
                return false;
            }
        }
        else if (definition.MinimumDays is not (>= 1 and <= PricingValues.MaximumMinimumDays) || !string.IsNullOrWhiteSpace(definition.Code))
        {
            return false;
        }

        var discountValid = definition.DiscountType switch
        {
            PromotionDiscountType.Percentage => definition.DiscountPercent is { } percent
                && PricingValues.IsPercentage(percent, allowZero: false, allowHundred: true)
                && definition.DiscountAmount is null,
            PromotionDiscountType.FixedAmount => definition.DiscountAmount is { } amount
                && PricingValues.IsPositiveTnd(amount)
                && definition.DiscountPercent is null,
            _ => false,
        };

        return discountValid
            && IsOrdered(definition.BookableFrom, definition.BookableUntil)
            && IsOrdered(definition.PickupFrom, definition.PickupUntil)
            && (definition.CategoryIds ?? []).All(id => id != Guid.Empty)
            && (definition.Channels ?? []).All(Enum.IsDefined);
    }

    internal static Promotion Create(RatePlan plan, PromotionDefinition definition, int position) =>
        new(NewId(), plan, definition, position);

    internal Promotion CopyForNewPlan(RatePlan plan) => new(NewId(), plan, ToDefinition() with { Id = null }, Position);

    /// <summary>The content of this entity, as edited.</summary>
    public PromotionDefinition ToDefinition() => new(
        Name,
        Kind,
        DiscountType,
        DiscountPercent,
        DiscountAmount,
        Code,
        MinimumDays,
        BookableFrom,
        BookableUntil,
        PickupFrom,
        PickupUntil,
        CategoryIds,
        Channels,
        IsCombinable,
        IsActive,
        Id);

    internal void Apply(PromotionDefinition definition, int position)
    {
        Name = definition.Name.Trim();
        Kind = definition.Kind;
        Code = definition.Kind == PromotionKind.Code ? NormalizeCode(definition.Code) : null;
        MinimumDays = definition.Kind == PromotionKind.Code ? null : definition.MinimumDays;
        DiscountType = definition.DiscountType;
        DiscountPercent = definition.DiscountType == PromotionDiscountType.Percentage ? definition.DiscountPercent : null;
        DiscountAmount = definition.DiscountType == PromotionDiscountType.FixedAmount ? definition.DiscountAmount : null;
        BookableFrom = definition.BookableFrom;
        BookableUntil = definition.BookableUntil;
        PickupFrom = definition.PickupFrom;
        PickupUntil = definition.PickupUntil;
        CategoryIds = [.. (definition.CategoryIds ?? []).Distinct().Order()];
        Channels = [.. (definition.Channels ?? []).Distinct().Order()];
        IsCombinable = definition.IsCombinable;
        IsActive = definition.IsActive;
        Position = position;
    }

    private static bool IsOrdered(DateOnly? from, DateOnly? until) => from is null || until is null || from <= until;
}

/// <summary>Content of a promotion. <paramref name="Id"/> identifies an existing promotion to update.</summary>
public sealed record PromotionDefinition(
    string Name,
    PromotionKind Kind,
    PromotionDiscountType DiscountType,
    decimal? DiscountPercent = null,
    Money? DiscountAmount = null,
    string? Code = null,
    int? MinimumDays = null,
    DateOnly? BookableFrom = null,
    DateOnly? BookableUntil = null,
    DateOnly? PickupFrom = null,
    DateOnly? PickupUntil = null,
    IReadOnlyList<Guid>? CategoryIds = null,
    IReadOnlyList<SalesChannel>? Channels = null,
    bool IsCombinable = false,
    bool IsActive = true,
    Guid? Id = null);
