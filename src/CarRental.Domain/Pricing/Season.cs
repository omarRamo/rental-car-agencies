using CarRental.Domain.Common;

namespace CarRental.Domain.Pricing;

/// <summary>
/// A dated season of a rate plan (basse, moyenne, haute saison): an inclusive range of Tunis calendar dates. Seasons of
/// a plan never overlap; daily prices are entered per <see cref="Level"/>. A season may impose a minimum rental duration.
/// </summary>
public sealed class Season : Entity, IAgencyOwned
{
    private Season()
    {
    }

    private Season(Guid id, RatePlan plan, SeasonDefinition definition)
        : base(id)
    {
        RatePlanId = plan.Id;
        AgencyId = plan.AgencyId;
        Apply(definition);
    }

    public Guid RatePlanId { get; private set; }

    /// <summary>The agency of the plan (ADR-0067).</summary>
    public Guid AgencyId { get; private set; }

    public SeasonLevel Level { get; private set; }

    /// <summary>First date of the season (Tunis calendar), included.</summary>
    public DateOnly StartDate { get; private set; }

    /// <summary>Last date of the season (Tunis calendar), included.</summary>
    public DateOnly EndDate { get; private set; }

    /// <summary>Minimum rental duration in days for any stay touching this season (0: none).</summary>
    public int MinimumDays { get; private set; }

    public bool Covers(DateOnly date) => date >= StartDate && date <= EndDate;

    internal static bool IsValid(SeasonDefinition definition) =>
        Enum.IsDefined(definition.Level)
        && definition.StartDate <= definition.EndDate
        && PricingValues.IsMinimumDays(definition.MinimumDays);

    internal static Season Create(RatePlan plan, SeasonDefinition definition) => new(NewId(), plan, definition);

    internal Season CopyForNewPlan(RatePlan plan) => new(NewId(), plan, ToDefinition() with { Id = null });

    /// <summary>The content of this entity, as edited.</summary>
    public SeasonDefinition ToDefinition() => new(Level, StartDate, EndDate, MinimumDays, Id);

    internal void Apply(SeasonDefinition definition)
    {
        Level = definition.Level;
        StartDate = definition.StartDate;
        EndDate = definition.EndDate;
        MinimumDays = definition.MinimumDays;
    }
}

/// <summary>Content of a season. <paramref name="Id"/> identifies an existing season to update (new season when null or unknown).</summary>
public sealed record SeasonDefinition(SeasonLevel Level, DateOnly StartDate, DateOnly EndDate, int MinimumDays = 0, Guid? Id = null);
