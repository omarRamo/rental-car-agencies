using CarRental.Domain.Common;

namespace CarRental.Domain.Pricing;

/// <summary>
/// A duration tier (palier de durée): from <see cref="MinimumDays"/> rental days up to the day before the next tier
/// (the last tier is open-ended). Tiers start at day 1 and are contiguous by construction. The discount applies to the
/// sum of the daily prices; a category may override it (<see cref="TierCategoryDiscount"/>).
/// </summary>
public sealed class DurationTier : Entity, IAgencyOwned
{
    private DurationTier()
    {
    }

    private DurationTier(Guid id, RatePlan plan, int minimumDays, decimal discountPercent)
        : base(id)
    {
        RatePlanId = plan.Id;
        AgencyId = plan.AgencyId;
        MinimumDays = minimumDays;
        DiscountPercent = discountPercent;
    }

    public Guid RatePlanId { get; private set; }

    /// <summary>The agency of the plan (ADR-0067).</summary>
    public Guid AgencyId { get; private set; }

    /// <summary>First rental day count of the tier (1 for the first tier).</summary>
    public int MinimumDays { get; private set; }

    /// <summary>Discount in percent (0 to 99.99, two decimals at most).</summary>
    public decimal DiscountPercent { get; private set; }

    internal static DurationTier Create(RatePlan plan, DurationTierDefinition definition) =>
        new(NewId(), plan, definition.MinimumDays, definition.DiscountPercent);

    internal DurationTier CopyForNewPlan(RatePlan plan) => new(NewId(), plan, MinimumDays, DiscountPercent);

    internal void ChangeDiscount(decimal discountPercent) => DiscountPercent = discountPercent;
}

/// <summary>A per-category discount of the tier starting at <see cref="TierMinimumDays"/>, replacing the plan percentage.</summary>
public sealed class TierCategoryDiscount : Entity, IAgencyOwned
{
    private TierCategoryDiscount()
    {
    }

    private TierCategoryDiscount(Guid id, RatePlan plan, int tierMinimumDays, Guid categoryId, decimal discountPercent)
        : base(id)
    {
        RatePlanId = plan.Id;
        AgencyId = plan.AgencyId;
        TierMinimumDays = tierMinimumDays;
        CategoryId = categoryId;
        DiscountPercent = discountPercent;
    }

    public Guid RatePlanId { get; private set; }

    /// <summary>The agency of the plan (ADR-0067).</summary>
    public Guid AgencyId { get; private set; }

    public int TierMinimumDays { get; private set; }

    public Guid CategoryId { get; private set; }

    public decimal DiscountPercent { get; private set; }

    internal static TierCategoryDiscount Create(RatePlan plan, TierCategoryDiscountDefinition definition) =>
        new(NewId(), plan, definition.TierMinimumDays, definition.CategoryId, definition.DiscountPercent);

    internal TierCategoryDiscount CopyForNewPlan(RatePlan plan) => new(NewId(), plan, TierMinimumDays, CategoryId, DiscountPercent);

    internal void ChangeDiscount(decimal discountPercent) => DiscountPercent = discountPercent;
}

/// <summary>A tier: from <paramref name="MinimumDays"/> rental days, <paramref name="DiscountPercent"/> % off the rental days.</summary>
public sealed record DurationTierDefinition(int MinimumDays, decimal DiscountPercent);

/// <summary>Discount of the tier starting at <paramref name="TierMinimumDays"/> for one category.</summary>
public sealed record TierCategoryDiscountDefinition(int TierMinimumDays, Guid CategoryId, decimal DiscountPercent);
