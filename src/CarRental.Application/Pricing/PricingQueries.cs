using CarRental.Application.Pricing.Contracts;
using CarRental.Domain.Common;
using CarRental.Domain.Pricing;

namespace CarRental.Application.Pricing;

/// <summary><see cref="IPricingQueries"/> over the published plans (no permission: used by the public funnel and the bot).</summary>
internal sealed class PricingQueries(IRatePlanStore store, RatePlanSelector selector) : IPricingQueries
{
    public async Task<IReadOnlyList<ExtraOffer>> GetExtrasAsync(Guid agencyId, SalesChannel channel, CancellationToken cancellationToken = default)
    {
        var plans = await selector.SelectAsync(agencyId, channel, replacement: null, cancellationToken);
        if (plans.IsFailure)
        {
            return [];
        }

        return
        [
            .. plans.Value.Grid.Extras.Where(e => e.IsActive).Select(e => new ExtraOffer(
                e.Code,
                e.Name,
                e.NameEn,
                (ExtraKindInfo)e.Kind,
                (ExtraPricingModeInfo)e.Mode,
                e.Price,
                e.CapPerUnit,
                e.LocationId,
                e.IsSelectable)),
        ];
    }

    public async Task<RatePlanSummary?> GetPublishedPlanAsync(Guid agencyId, SalesChannel? channel = null, CancellationToken cancellationToken = default)
    {
        var plan = await store.FindPublishedAsync(agencyId, channel, cancellationToken);
        return plan is null ? null : ToSummary(plan);
    }

    internal static RatePlanSummary ToSummary(RatePlan plan) => new(
        plan.Id,
        plan.AgencyId,
        plan.Channel,
        plan.Name,
        plan.Version ?? 0,
        plan.PublishedAtUtc ?? plan.ModifiedAtUtc,
        [.. plan.Seasons.Select(s => new SeasonInfo((SeasonLevelInfo)s.Level, s.StartDate, s.EndDate, s.MinimumDays))],
        [.. plan.Events.Select(e => new PriorityEventInfo(e.Id, e.Name, e.StartDate, e.EndDate, e.Priority, e.MinimumDays))],
        [
            .. plan.PricedCategoryIds.Select(categoryId => new CategoryRateInfo(
                categoryId,
                plan.FindPrice(categoryId, RatePeriodKey.Base)!.Value,
                plan.FindPrice(categoryId, RatePeriodKey.ForSeason(SeasonLevel.Low)),
                plan.FindPrice(categoryId, RatePeriodKey.ForSeason(SeasonLevel.Mid)),
                plan.FindPrice(categoryId, RatePeriodKey.ForSeason(SeasonLevel.High)),
                plan.Prices
                    .Where(p => p.CategoryId == categoryId && p.EventId is not null)
                    .ToDictionary(p => p.EventId!.Value, p => p.DailyPrice))),
        ],
        [.. plan.ChannelRules.Select(r => new ChannelRuleInfo(r.Channel, (ChannelPricingModeInfo)r.Mode, r.MarkupPercent))]);
}
