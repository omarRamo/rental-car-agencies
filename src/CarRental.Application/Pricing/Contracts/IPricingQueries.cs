using CarRental.Domain.Common;

namespace CarRental.Application.Pricing.Contracts;

/// <summary>Public read API of the Pricing module for other modules (ids and contract DTOs only).</summary>
public interface IPricingQueries
{
    /// <summary>
    /// Active extras of the plan pricing <paramref name="channel"/> (the channel's own plan, else the default plan), in
    /// catalogue order, with their price rules: the booking funnel offers the selectable ones and explains the automatic
    /// ones. Empty when the agency has no published plan for the channel.
    /// </summary>
    Task<IReadOnlyList<ExtraOffer>> GetExtrasAsync(Guid agencyId, SalesChannel channel, CancellationToken cancellationToken = default);

    /// <summary>The published default plan of the agency (or the own plan of <paramref name="channel"/>), or <see langword="null"/>.</summary>
    Task<RatePlanSummary?> GetPublishedPlanAsync(Guid agencyId, SalesChannel? channel = null, CancellationToken cancellationToken = default);
}

/// <summary>
/// An extra and its price rule (VAT included): <see cref="Price"/> per day (with an optional cap per unit for the whole
/// rental) or per rental. Selectable extras are chosen by <see cref="Code"/>; the others are added automatically.
/// <see cref="NameEn"/> is <see langword="null"/> when the English name is the French one.
/// </summary>
public sealed record ExtraOffer(
    string Code,
    string Name,
    string? NameEn,
    ExtraKindInfo Kind,
    ExtraPricingModeInfo Mode,
    Money Price,
    Money? CapPerUnit,
    Guid? LocationId,
    bool IsSelectable);

/// <summary>Kind of an extra (same values as the domain enum).</summary>
public enum ExtraKindInfo
{
    /// <summary>Chosen with a quantity (additional driver, child seat, GPS, hotel delivery…).</summary>
    Optional = 1,

    /// <summary>Chosen (quantity 1); the deposit becomes the reduced deposit of the category.</summary>
    FranchiseWaiver = 2,

    /// <summary>Automatic, per driver younger than <c>drivers.young_driver_age_below</c> at pick-up.</summary>
    YoungDriver = 3,

    /// <summary>Automatic, when the pick-up and/or the return is at <see cref="ExtraOffer.LocationId"/>.</summary>
    LocationDelivery = 4,

    /// <summary>Automatic, per pick-up or return outside <c>pricing.opening_hours</c>.</summary>
    AfterHours = 5,

    /// <summary>Automatic, when the return location differs from the pick-up location.</summary>
    OneWay = 6,
}

/// <summary>How an extra is charged (same values as the domain enum).</summary>
public enum ExtraPricingModeInfo
{
    PerDay = 1,
    PerRental = 2,
}

/// <summary>A published rate plan: seasons, events, the daily prices TTC of each category and the channel rules.</summary>
public sealed record RatePlanSummary(
    Guid Id,
    Guid AgencyId,
    SalesChannel? Channel,
    string Name,
    int Version,
    DateTimeOffset PublishedAtUtc,
    IReadOnlyList<SeasonInfo> Seasons,
    IReadOnlyList<PriorityEventInfo> Events,
    IReadOnlyList<CategoryRateInfo> CategoryRates,
    IReadOnlyList<ChannelRuleInfo> ChannelRules);

/// <summary>Season level (same values as the domain enum).</summary>
public enum SeasonLevelInfo
{
    Low = 1,
    Mid = 2,
    High = 3,
}

/// <summary>A dated season (inclusive Tunis dates) and its minimum rental duration (0: none).</summary>
public sealed record SeasonInfo(SeasonLevelInfo Level, DateOnly StartDate, DateOnly EndDate, int MinimumDays);

/// <summary>A priority event (inclusive Tunis dates); the highest priority wins on overlapping dates.</summary>
public sealed record PriorityEventInfo(Guid Id, string Name, DateOnly StartDate, DateOnly EndDate, int Priority, int MinimumDays);

/// <summary>
/// Daily prices TTC of a category: base price and season levels (<see langword="null"/>: the base price applies), and
/// event prices by event id.
/// </summary>
public sealed record CategoryRateInfo(
    Guid CategoryId,
    Money BasePrice,
    Money? LowSeasonPrice,
    Money? MidSeasonPrice,
    Money? HighSeasonPrice,
    IReadOnlyDictionary<Guid, Money> EventPrices);

/// <summary>Rule of a channel in the default plan (a channel without rule is at parity).</summary>
public sealed record ChannelRuleInfo(SalesChannel Channel, ChannelPricingModeInfo Mode, decimal? MarkupPercent);
