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

    private ChannelRule(Guid id, ChannelRuleDefinition definition)
        : base(id) => Apply(definition);

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

    internal static ChannelRule Create(ChannelRuleDefinition definition) => new(NewId(), definition);

    internal ChannelRule CopyForNewPlan() => new(NewId(), new ChannelRuleDefinition(Channel, Mode, MarkupPercent));

    internal ChannelRuleDefinition ToDefinition() => new(Channel, Mode, MarkupPercent);

    internal void Apply(ChannelRuleDefinition definition)
    {
        Channel = definition.Channel;
        Mode = definition.Mode;
        MarkupPercent = definition.MarkupPercent;
    }
}

/// <summary>Rule of one channel.</summary>
public sealed record ChannelRuleDefinition(SalesChannel Channel, ChannelPricingMode Mode, decimal? MarkupPercent = null);
