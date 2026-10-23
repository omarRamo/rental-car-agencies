using CarRental.Domain.Common;

namespace CarRental.Domain.Pricing;

/// <summary>
/// A priority event of a rate plan (Aïd, fin d'année, festival): an inclusive range of Tunis calendar dates whose prices
/// take precedence over the seasons. When events overlap, the highest <see cref="Priority"/> wins (overlapping events
/// never share a priority). An event may impose a minimum rental duration.
/// </summary>
public sealed class PriorityEvent : Entity, IAgencyOwned
{
    public const int NameMaxLength = 80;
    public const int MaximumPriority = 1000;

    private PriorityEvent()
    {
    }

    private PriorityEvent(Guid id, RatePlan plan, PriorityEventDefinition definition)
        : base(id)
    {
        RatePlanId = plan.Id;
        AgencyId = plan.AgencyId;
        Apply(definition);
    }

    public Guid RatePlanId { get; private set; }

    /// <summary>The agency of the plan (ADR-0067).</summary>
    public Guid AgencyId { get; private set; }

    public string Name { get; private set; } = null!;

    public DateOnly StartDate { get; private set; }

    public DateOnly EndDate { get; private set; }

    /// <summary>Higher wins between overlapping events (1 to <see cref="MaximumPriority"/>).</summary>
    public int Priority { get; private set; }

    public int MinimumDays { get; private set; }

    public bool Covers(DateOnly date) => date >= StartDate && date <= EndDate;

    public bool Overlaps(PriorityEvent other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return StartDate <= other.EndDate && other.StartDate <= EndDate;
    }

    internal static bool IsValid(PriorityEventDefinition definition) =>
        (definition.Name ?? string.Empty).Trim() is { Length: > 0 and <= NameMaxLength }
        && definition.StartDate <= definition.EndDate
        && definition.Priority is >= 1 and <= MaximumPriority
        && PricingValues.IsMinimumDays(definition.MinimumDays);

    internal static PriorityEvent Create(RatePlan plan, PriorityEventDefinition definition) => new(NewId(), plan, definition);

    internal PriorityEvent CopyForNewPlan(RatePlan plan) => new(NewId(), plan, ToDefinition() with { Id = null });

    /// <summary>The content of this entity, as edited.</summary>
    public PriorityEventDefinition ToDefinition() => new(Name, StartDate, EndDate, Priority, MinimumDays, Id);

    internal void Apply(PriorityEventDefinition definition)
    {
        Name = definition.Name.Trim();
        StartDate = definition.StartDate;
        EndDate = definition.EndDate;
        Priority = definition.Priority;
        MinimumDays = definition.MinimumDays;
    }
}

/// <summary>Content of a priority event. <paramref name="Id"/> identifies an existing event to update (its prices are kept).</summary>
public sealed record PriorityEventDefinition(
    string Name,
    DateOnly StartDate,
    DateOnly EndDate,
    int Priority = 1,
    int MinimumDays = 0,
    Guid? Id = null);
