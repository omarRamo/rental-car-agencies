using CarRental.Domain.Common;

namespace CarRental.Domain.Pricing;

/// <summary>
/// How a sales channel is priced, held by the default plan of the agency: parity with the default grid, a markup
/// percentage on its daily prices (e.g. Booking.com commission), or the channel's own published plan. A channel without
/// rule is priced at parity.
/// </summary>
public sealed class ChannelRule : Entity
{
    private ChannelRule()
    {
    }

    private ChannelRule(Guid id, Guid ratePlanId, ChannelRuleDefinition definition)
        : base(id)
    {
        RatePlanId = ratePlanId;
        Apply(definition);
    }

    public Guid RatePlanId { get; private set; }

    public SalesChannel Channel { get; private set; }

    public ChannelPricingMode Mode { get; private set; }

    /// <summary>Markup percentage of a <see cref="ChannelPricingMode.Markup"/> rule (above 0, up to 100).</summary>
    public decimal? MarkupPercent { get; private set; }

    internal static bool IsValid(ChannelRuleDefinition definition) =>
        Enum.IsDefined(definition.Channel)
        && Enum.IsDefined(definition.Mode)
        && (definition.Mode == ChannelPricingMode.Markup
            ? definition.MarkupPercent is { } percent && PricingValues.IsPercentage(percent, allowZero: false, allowHundred: true)
            : definition.MarkupPercent is null);

    internal static ChannelRule Create(Guid ratePlanId, ChannelRuleDefinition definition) => new(NewId(), ratePlanId, definition);

    internal ChannelRule CopyForNewPlan(Guid ratePlanId) => new(NewId(), ratePlanId, ToDefinition());

    /// <summary>The content of this entity, as edited.</summary>
    public ChannelRuleDefinition ToDefinition() => new(Channel, Mode, MarkupPercent);

    internal void Apply(ChannelRuleDefinition definition)
    {
        Channel = definition.Channel;
        Mode = definition.Mode;
        MarkupPercent = definition.MarkupPercent;
    }
}

/// <summary>Rule of one channel.</summary>
public sealed record ChannelRuleDefinition(SalesChannel Channel, ChannelPricingMode Mode, decimal? MarkupPercent = null);
