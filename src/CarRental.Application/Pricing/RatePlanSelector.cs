using CarRental.Domain.Common;
using CarRental.Domain.Pricing;

namespace CarRental.Application.Pricing;

/// <summary>
/// Chooses the plans that price a channel (ADR-0024): the published default plan of the agency holds the channel rules;
/// its rule for the channel is parity (no rule), a markup on its own grid, or <see cref="ChannelPricingMode.OwnGrid"/>,
/// in which case the published plan of the channel prices it.
/// </summary>
internal sealed class RatePlanSelector(IRatePlanStore store)
{
    /// <summary>
    /// The plans for <paramref name="channel"/>. <paramref name="replacement"/> (simulator) stands for the published plan of
    /// its own agency and channel: a default draft brings its own channel rules; a channel draft is used as the grid of
    /// its channel whatever the rule says.
    /// </summary>
    public async Task<Result<SelectedPlans>> SelectAsync(
        Guid agencyId,
        SalesChannel channel,
        RatePlan? replacement,
        CancellationToken cancellationToken)
    {
        var defaultPlan = replacement is { IsDefaultPlan: true }
            ? replacement
            : await store.FindPublishedAsync(agencyId, null, cancellationToken);
        if (defaultPlan is null)
        {
            return PricingErrors.NoPublishedPlan;
        }

        if (replacement is { IsDefaultPlan: false } channelPlan && channelPlan.Channel == channel)
        {
            return new SelectedPlans(defaultPlan, channelPlan, ChannelPricing.OwnGrid);
        }

        var pricing = ChannelPricing.From(defaultPlan.FindChannelRule(channel));
        if (pricing.Mode != ChannelPricingMode.OwnGrid)
        {
            return new SelectedPlans(defaultPlan, defaultPlan, pricing);
        }

        var grid = await store.FindPublishedAsync(agencyId, channel, cancellationToken);
        return grid is null ? PricingErrors.ChannelPlanMissing : new SelectedPlans(defaultPlan, grid, pricing);
    }
}

/// <summary>The default plan (channel rules), the plan whose grid prices the request, and the channel rule applied.</summary>
internal sealed record SelectedPlans(RatePlan DefaultPlan, RatePlan Grid, ChannelPricing ChannelPricing);
