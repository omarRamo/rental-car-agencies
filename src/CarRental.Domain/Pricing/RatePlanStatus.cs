namespace CarRental.Domain.Pricing;

/// <summary>Life cycle of a rate plan: drafts are edited, one published plan per agency and channel prices, archives stay for history.</summary>
public enum RatePlanStatus
{
    /// <summary>Being edited; never used for real quotes (only by the simulator).</summary>
    Draft = 1,

    /// <summary>The plan in force for its agency and channel; immutable.</summary>
    Published = 2,

    /// <summary>Replaced by a newer version or withdrawn; kept for history and for the quotes that reference it.</summary>
    Archived = 3,
}

/// <summary>Part of a rate plan changed by an edit (carried by <see cref="RatePlanDraftUpdated"/>).</summary>
public enum RatePlanSection
{
    Name = 1,
    Seasons = 2,
    Events = 3,
    Prices = 4,
    Tiers = 5,
    Extras = 6,
    Promotions = 7,
    ChannelRules = 8,
}
