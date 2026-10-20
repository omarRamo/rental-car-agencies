using CarRental.Application.Pricing.Contracts;
using CarRental.Domain.Common;
using CarRental.Domain.Pricing;

namespace CarRental.Application.Pricing.Administration;

/// <summary>A rate plan in the back-office list (no content).</summary>
public sealed record RatePlanListItem(
    Guid Id,
    Guid AgencyId,
    SalesChannel? Channel,
    string Name,
    RatePlanStatus Status,
    int? Version,
    int Revision,
    Guid? BasedOnPlanId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ModifiedAtUtc,
    DateTimeOffset? PublishedAtUtc,
    DateTimeOffset? ArchivedAtUtc);

/// <summary>
/// A rate plan as edited in the back office: its header and its content as definitions (with the ids of the existing
/// children, sent back unchanged to update them), plus the validation issues of the plan.
/// </summary>
public sealed record RatePlanDetail(
    RatePlanListItem Plan,
    IReadOnlyList<SeasonDefinition> Seasons,
    IReadOnlyList<PriorityEventDefinition> Events,
    IReadOnlyList<CategoryPriceDefinition> Prices,
    IReadOnlyList<DurationTierDefinition> Tiers,
    IReadOnlyList<TierCategoryDiscountDefinition> TierCategoryDiscounts,
    IReadOnlyList<ExtraDefinition> Extras,
    IReadOnlyList<PromotionDefinition> Promotions,
    IReadOnlyList<ChannelRuleDefinition> ChannelRules,
    IReadOnlyList<PlanValidationIssue> Issues)
{
    public bool IsEditable => Plan.Status == RatePlanStatus.Draft;

    public bool CanBePublished => IsEditable && !Issues.Any(i => i.IsBlocking);
}

/// <summary>
/// A new draft for an agency and channel (<see langword="null"/>: the default plan). It copies <see cref="SourcePlanId"/>
/// when given (any version of the same agency), else the published plan of the channel, else the published default
/// plan (first own grid of a channel), else it starts empty.
/// </summary>
public sealed record CreateDraftRequest(Guid AgencyId, SalesChannel? Channel, string? Name = null, Guid? SourcePlanId = null);

/// <summary>
/// A simulation: <see cref="Request"/> priced with the published plans, and with <see cref="ComparedPlanId"/> (a draft or
/// an old version) standing for the published plan of its agency and channel. <see cref="QuotedAt"/> is the booking
/// instant to simulate (now by default): it drives early booking promotions and booking windows.
/// </summary>
public sealed record SimulationRequest(PriceRequest Request, DateTimeOffset? QuotedAt = null, Guid? ComparedPlanId = null);

/// <summary>Both quotes of a simulation, each a price or the error explaining why there is none.</summary>
public sealed record SimulationResult(QuoteOutcome Published, QuoteOutcome? Compared);

/// <summary>A quote or its error.</summary>
public sealed record QuoteOutcome(PriceQuote? Quote, Error? Error)
{
    public bool IsSuccess => Quote is not null;

    public static QuoteOutcome From(Result<PriceQuote> result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.IsSuccess ? new QuoteOutcome(result.Value, null) : new QuoteOutcome(null, result.Error);
    }
}

internal static class RatePlanMapper
{
    public static RatePlanListItem ToListItem(RatePlan plan) => new(
        plan.Id,
        plan.AgencyId,
        plan.Channel,
        plan.Name,
        plan.Status,
        plan.Version,
        plan.Revision,
        plan.BasedOnPlanId,
        plan.CreatedAtUtc,
        plan.ModifiedAtUtc,
        plan.PublishedAtUtc,
        plan.ArchivedAtUtc);

    public static RatePlanDetail ToDetail(RatePlan plan, IReadOnlyList<PlanValidationIssue> issues) => new(
        ToListItem(plan),
        [.. plan.Seasons.Select(s => s.ToDefinition())],
        [.. plan.Events.Select(e => e.ToDefinition())],
        [.. plan.Prices.Select(p => p.ToDefinition())],
        [.. plan.Tiers.Select(t => new DurationTierDefinition(t.MinimumDays, t.DiscountPercent))],
        [.. plan.TierCategoryDiscounts.Select(d => new TierCategoryDiscountDefinition(d.TierMinimumDays, d.CategoryId, d.DiscountPercent))],
        [.. plan.Extras.Select(e => e.ToDefinition())],
        [.. plan.Promotions.Select(p => p.ToDefinition())],
        [.. plan.ChannelRules.Select(r => r.ToDefinition())],
        issues);
}
