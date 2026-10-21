using CarRental.Domain.Common;

namespace CarRental.Domain.Channels;

/// <summary>
/// A commercial restriction of a channel on a vehicle category over inclusive Tunis dates (SPEC §7 "Quotas et arrêt des
/// ventes par canal"): either vehicles kept for the other channels (<see cref="ChannelQuotaKind.Reserve"/>: "keep 2
/// vehicles out of Booking.com in high season") or a stop-sell (<see cref="ChannelQuotaKind.StopSell"/>). The
/// availability engine applies them everywhere through <c>IChannelAvailabilityPolicy</c>; a stop-sell limited to one
/// pick-up location is applied by the Channels module itself (the engine has no location).
/// </summary>
public sealed class ChannelQuota : AggregateRoot, ISoftDeletable
{
    public const int NoteMaxLength = 300;

    /// <summary>Longest restriction (two years): a longer one is a typing mistake.</summary>
    public const int MaxDays = 731;

    public const int MaxReservedVehicles = 999;

    private ChannelQuota()
    {
    }

    private ChannelQuota(Channel channel, Guid categoryId, ChannelQuotaKind kind, ChannelStamp stamp)
        : base(NewId())
    {
        ChannelId = channel.Id;
        AgencyId = channel.AgencyId;
        SalesChannel = channel.SalesChannel;
        CategoryId = categoryId;
        Kind = kind;
        CreatedAtUtc = stamp.At;
        CreatedBy = stamp.By;
        UpdatedAtUtc = stamp.At;
        UpdatedBy = stamp.By;
    }

    public Guid ChannelId { get; private set; }

    public Guid AgencyId { get; private set; }

    /// <summary>The sales channel of <see cref="ChannelId"/> (read by the availability policy without a join).</summary>
    public SalesChannel SalesChannel { get; private set; }

    public Guid CategoryId { get; private set; }

    /// <summary>The pick-up location of a stop-sell limited to one place; <see langword="null"/> for the whole category.</summary>
    public Guid? LocationId { get; private set; }

    public ChannelQuotaKind Kind { get; private set; }

    /// <summary>Vehicles kept for the other channels (<see cref="ChannelQuotaKind.Reserve"/>); 0 for a stop-sell.</summary>
    public int ReservedVehicles { get; private set; }

    /// <summary>First Tunis day of the restriction.</summary>
    public DateOnly StartDate { get; private set; }

    /// <summary>Last Tunis day of the restriction (included).</summary>
    public DateOnly EndDate { get; private set; }

    public string? Note { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public string? CreatedBy { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public string? UpdatedBy { get; private set; }

    public bool IsDeleted { get; private set; }

    public DateTimeOffset? DeletedAtUtc { get; private set; }

    /// <summary>The instants covered: from the start of <see cref="StartDate"/> to the end of <see cref="EndDate"/> (Tunis).</summary>
    public Period Period => ChannelRestrictionRule.PeriodOf(StartDate, EndDate);

    public ChannelRestrictionRule ToRule() => new(Kind, LocationId, StartDate, EndDate, ReservedVehicles);

    /// <summary>Keeps <paramref name="vehicles"/> of the category out of the channel (for the other channels) on the dates.</summary>
    public static Result<ChannelQuota> Reserve(Channel channel, Guid categoryId, DateOnly startDate, DateOnly endDate, int vehicles, string? note, ChannelStamp stamp) =>
        Create(channel, categoryId, ChannelQuotaKind.Reserve, null, startDate, endDate, vehicles, note, stamp);

    /// <summary>Stops the sales of the category on the channel on the dates, everywhere or at one pick-up location.</summary>
    public static Result<ChannelQuota> StopSell(Channel channel, Guid categoryId, Guid? locationId, DateOnly startDate, DateOnly endDate, string? note, ChannelStamp stamp) =>
        Create(channel, categoryId, ChannelQuotaKind.StopSell, locationId, startDate, endDate, 0, note, stamp);

    /// <summary>Changes the dates, the vehicles (a reservation), the location (a stop-sell) and the note.</summary>
    public Result Change(Guid? locationId, DateOnly startDate, DateOnly endDate, int vehicles, string? note, ChannelStamp stamp)
    {
        ArgumentNullException.ThrowIfNull(stamp);
        var valid = Validate(Kind, locationId, startDate, endDate, vehicles, note, stamp);
        if (valid.IsFailure)
        {
            return valid.Error;
        }

        LocationId = locationId;
        StartDate = startDate;
        EndDate = endDate;
        ReservedVehicles = Kind == ChannelQuotaKind.Reserve ? vehicles : 0;
        Note = valid.Value;
        UpdatedAtUtc = stamp.At;
        UpdatedBy = stamp.By;
        Raise(new ChannelQuotaChanged(Id, ChannelId, CategoryId, Kind));
        return Result.Success();
    }

    /// <summary>Records the removal (the persistence layer then marks the row deleted).</summary>
    public void MarkRemoved(ChannelStamp stamp)
    {
        ArgumentNullException.ThrowIfNull(stamp);
        UpdatedAtUtc = stamp.At;
        UpdatedBy = stamp.By;
        Raise(new ChannelQuotaRemoved(Id, ChannelId, CategoryId, Kind));
    }

    private static Result<ChannelQuota> Create(
        Channel channel,
        Guid categoryId,
        ChannelQuotaKind kind,
        Guid? locationId,
        DateOnly startDate,
        DateOnly endDate,
        int vehicles,
        string? note,
        ChannelStamp stamp)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(stamp);
        if (categoryId == Guid.Empty)
        {
            return ChannelsErrors.RestrictionCategoryRequired;
        }

        var valid = Validate(kind, locationId, startDate, endDate, vehicles, note, stamp);
        if (valid.IsFailure)
        {
            return valid.Error;
        }

        var quota = new ChannelQuota(channel, categoryId, kind, stamp)
        {
            LocationId = locationId,
            StartDate = startDate,
            EndDate = endDate,
            ReservedVehicles = kind == ChannelQuotaKind.Reserve ? vehicles : 0,
            Note = valid.Value,
        };
        quota.Raise(new ChannelQuotaAdded(quota.Id, quota.ChannelId, categoryId, kind));
        return quota;
    }

    /// <summary>The checked note (trimmed, <see langword="null"/> when empty).</summary>
    private static Result<string?> Validate(ChannelQuotaKind kind, Guid? locationId, DateOnly startDate, DateOnly endDate, int vehicles, string? note, ChannelStamp stamp)
    {
        if (endDate < startDate || endDate.DayNumber - startDate.DayNumber + 1 > MaxDays)
        {
            return ChannelsErrors.RestrictionPeriodInvalid;
        }

        if (endDate < stamp.Today)
        {
            return ChannelsErrors.RestrictionEnded;
        }

        if (kind == ChannelQuotaKind.Reserve)
        {
            if (vehicles is < 1 or > MaxReservedVehicles)
            {
                return ChannelsErrors.RestrictionVehiclesInvalid;
            }

            if (locationId is not null)
            {
                // The engine has no location: a reservation of vehicles is for the whole category.
                return ChannelsErrors.RestrictionLocationNotAllowed;
            }
        }

        if (locationId == Guid.Empty)
        {
            return ChannelsErrors.RestrictionLocationNotAllowed;
        }

        var trimmed = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        return trimmed?.Length > NoteMaxLength ? ChannelsErrors.RestrictionNoteTooLong : Result.Success(trimmed);
    }
}

/// <summary>
/// A restriction as the rules read it (<see cref="ChannelQuota.ToRule"/>), independent of persistence: kind, location
/// of a located stop-sell, inclusive Tunis dates and reserved vehicles.
/// </summary>
public sealed record ChannelRestrictionRule(ChannelQuotaKind Kind, Guid? LocationId, DateOnly StartDate, DateOnly EndDate, int ReservedVehicles)
{
    public Period Period => PeriodOf(StartDate, EndDate);

    /// <summary>From the start of <paramref name="startDate"/> to the start of the day after <paramref name="endDate"/> (Tunis).</summary>
    public static Period PeriodOf(DateOnly startDate, DateOnly endDate) =>
        Period.Create(TunisTime.StartOfDay(startDate), TunisTime.StartOfDay(endDate.AddDays(1))).Value;
}

/// <summary>A number of vehicles kept for the other channels during a period.</summary>
public sealed record ReservedPeriod(Period Period, int Vehicles);

/// <summary>
/// What the availability engine applies for one channel and one category over a range: the vehicles kept for the other
/// channels (overlapping reservations add up in the engine) and the stop-sell periods (merged).
/// </summary>
public sealed record ChannelRestrictionView(IReadOnlyList<ReservedPeriod> Reserved, IReadOnlyList<Period> StopSales)
{
    public static ChannelRestrictionView None { get; } = new([], []);

    public bool IsEmpty => Reserved.Count == 0 && StopSales.Count == 0;
}

/// <summary>
/// Pure rules of the channel restrictions (ADR-0052): the engine view over a range — reservations and stop-sells of the
/// whole category, clipped to the range — and the stop-sells limited to a pick-up location, applied by the Channels
/// module to its own messages.
/// </summary>
public static class ChannelRestrictionRules
{
    /// <summary>The restrictions of the whole category (location-less) that touch <paramref name="range"/>, clipped to it.</summary>
    public static ChannelRestrictionView ForEngine(IEnumerable<ChannelRestrictionRule> rules, Period range)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(range);
        var reserved = new List<ReservedPeriod>();
        var stopSales = new List<Period>();
        foreach (var rule in rules.Where(r => r.LocationId is null))
        {
            if (rule.Period.Intersect(range) is not { } clipped)
            {
                continue;
            }

            if (rule.Kind == ChannelQuotaKind.StopSell)
            {
                stopSales.Add(clipped);
            }
            else if (rule.ReservedVehicles > 0)
            {
                reserved.Add(new ReservedPeriod(clipped, rule.ReservedVehicles));
            }
        }

        return reserved.Count == 0 && stopSales.Count == 0
            ? ChannelRestrictionView.None
            : new ChannelRestrictionView([.. reserved.OrderBy(r => r.Period.Start)], Merge(stopSales));
    }

    /// <summary>The stop-sells of the pick-up location <paramref name="locationId"/> that touch <paramref name="range"/>, merged and clipped.</summary>
    public static IReadOnlyList<Period> StopSalesAt(IEnumerable<ChannelRestrictionRule> rules, Guid locationId, Period range)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(range);
        return Merge(
            [
                .. rules
                    .Where(r => r.Kind == ChannelQuotaKind.StopSell && r.LocationId == locationId)
                    .Select(r => r.Period.Intersect(range))
                    .OfType<Period>(),
            ]);
    }

    /// <summary>Whether a stop-sell of the pick-up location covers an instant of <paramref name="rental"/>.</summary>
    public static bool StopsSellingAt(IEnumerable<ChannelRestrictionRule> rules, Guid locationId, Period rental) =>
        StopSalesAt(rules, locationId, rental).Count > 0;

    /// <summary>Overlapping or touching periods merged, in order.</summary>
    public static IReadOnlyList<Period> Merge(IEnumerable<Period> periods)
    {
        ArgumentNullException.ThrowIfNull(periods);
        var merged = new List<Period>();
        foreach (var period in periods.OrderBy(p => p.Start))
        {
            if (merged.Count > 0 && period.Start <= merged[^1].End)
            {
                var last = merged[^1];
                merged[^1] = Period.Create(last.Start, period.End > last.End ? period.End : last.End).Value;
            }
            else
            {
                merged.Add(period);
            }
        }

        return merged;
    }
}
