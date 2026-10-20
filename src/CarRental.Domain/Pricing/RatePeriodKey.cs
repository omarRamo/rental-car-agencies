namespace CarRental.Domain.Pricing;

/// <summary>Season level of a dated season (basse, moyenne, haute saison). Prices are entered per level.</summary>
public enum SeasonLevel
{
    Low = 1,
    Mid = 2,
    High = 3,
}

/// <summary>Column of the price grid: the base price of a category, a season level or a priority event.</summary>
public enum RatePeriodKind
{
    Base = 0,
    LowSeason = 1,
    MidSeason = 2,
    HighSeason = 3,
    Event = 4,
}

/// <summary>
/// Identifies a column of the price grid (category × period): <see cref="Base"/>, a season level or a priority event
/// (<see cref="EventId"/> set only for <see cref="RatePeriodKind.Event"/>).
/// </summary>
public readonly record struct RatePeriodKey
{
    private RatePeriodKey(RatePeriodKind kind, Guid? eventId)
    {
        Kind = kind;
        EventId = eventId;
    }

    public RatePeriodKind Kind { get; }

    public Guid? EventId { get; }

    public static RatePeriodKey Base => new(RatePeriodKind.Base, null);

    public bool IsBase => Kind == RatePeriodKind.Base;

    public static RatePeriodKey ForSeason(SeasonLevel level) => level switch
    {
        SeasonLevel.Low => new(RatePeriodKind.LowSeason, null),
        SeasonLevel.Mid => new(RatePeriodKind.MidSeason, null),
        SeasonLevel.High => new(RatePeriodKind.HighSeason, null),
        _ => throw new ArgumentOutOfRangeException(nameof(level), level, "Unknown season level."),
    };

    public static RatePeriodKey ForEvent(Guid eventId)
    {
        if (eventId == Guid.Empty)
        {
            throw new ArgumentException("An event id cannot be empty.", nameof(eventId));
        }

        return new(RatePeriodKind.Event, eventId);
    }

    /// <summary>Rebuilds a key from its stored parts; <see langword="null"/> when they are inconsistent.</summary>
    public static RatePeriodKey? From(RatePeriodKind kind, Guid? eventId) => kind switch
    {
        RatePeriodKind.Base when eventId is null => Base,
        RatePeriodKind.LowSeason when eventId is null => ForSeason(SeasonLevel.Low),
        RatePeriodKind.MidSeason when eventId is null => ForSeason(SeasonLevel.Mid),
        RatePeriodKind.HighSeason when eventId is null => ForSeason(SeasonLevel.High),
        RatePeriodKind.Event when eventId is { } id && id != Guid.Empty => ForEvent(id),
        _ => null,
    };

    public override string ToString() => EventId is { } id ? $"{Kind}:{id}" : Kind.ToString();
}
