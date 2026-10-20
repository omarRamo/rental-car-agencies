using CarRental.Domain.Common;

namespace CarRental.Domain.Pricing;

/// <summary>
/// A duration tier (palier de durée): from <see cref="MinimumDays"/> rental days up to the day before the next tier
/// (the last tier is open-ended). Tiers start at day 1 and are contiguous by construction. The discount applies to the
/// sum of the daily prices; a category may override it (<see cref="TierCategoryDiscount"/>).
/// </summary>
public sealed class DurationTier : Entity
{
    private DurationTier()
    {
    }

    private DurationTier(Guid id, int minimumDays, decimal discountPercent)
        : base(id)
    {
        MinimumDays = minimumDays;
        DiscountPercent = discountPercent;
    }

    /// <summary>First rental day count of the tier (1 for the first tier).</summary>
    public int MinimumDays { get; private set; }

    /// <summary>Discount in percent (0 to 99.99, two decimals at most).</summary>
    public decimal DiscountPercent { get; private set; }

    internal static DurationTier Create(DurationTierDefinition definition) => new(NewId(), definition.MinimumDays, definition.DiscountPercent);

    internal DurationTier CopyForNewPlan() => new(NewId(), MinimumDays, DiscountPercent);

    internal void ChangeDiscount(decimal discountPercent) => DiscountPercent = discountPercent;
}

/// <summary>A per-category discount of the tier starting at <see cref="TierMinimumDays"/>, replacing the plan percentage.</summary>
public sealed class TierCategoryDiscount : Entity
{
    private TierCategoryDiscount()
    {
    }

    private TierCategoryDiscount(Guid id, int tierMinimumDays, Guid categoryId, decimal discountPercent)
        : base(id)
    {
        TierMinimumDays = tierMinimumDays;
        CategoryId = categoryId;
        DiscountPercent = discountPercent;
    }

    public int TierMinimumDays { get; private set; }

    public Guid CategoryId { get; private set; }

    public decimal DiscountPercent { get; private set; }

    internal static TierCategoryDiscount Create(TierCategoryDiscountDefinition definition) =>
        new(NewId(), definition.TierMinimumDays, definition.CategoryId, definition.DiscountPercent);

    internal TierCategoryDiscount CopyForNewPlan() => new(NewId(), TierMinimumDays, CategoryId, DiscountPercent);

    internal void ChangeDiscount(decimal discountPercent) => DiscountPercent = discountPercent;
}

/// <summary>A tier: from <paramref name="MinimumDays"/> rental days, <paramref name="DiscountPercent"/> % off the rental days.</summary>
public sealed record DurationTierDefinition(int MinimumDays, decimal DiscountPercent);

/// <summary>Discount of the tier starting at <paramref name="TierMinimumDays"/> for one category.</summary>
public sealed record TierCategoryDiscountDefinition(int TierMinimumDays, Guid CategoryId, decimal DiscountPercent);
