namespace CarRental.Domain.Common;

/// <summary>
/// The business time zone (Africa/Tunis). Instants are stored in UTC; rules on calendar days, opening hours
/// and rental units are evaluated in Tunis local time with these helpers. Works on Windows and Linux.
/// </summary>
public static class TunisTime
{
    /// <summary>IANA identifier of the business time zone.</summary>
    public const string TimeZoneId = "Africa/Tunis";

    private static readonly Lazy<TimeZoneInfo> LazyZone = new(ResolveZone);

    /// <summary>The Africa/Tunis time zone (IANA id, or its Windows equivalent when ICU is unavailable).</summary>
    public static TimeZoneInfo Zone => LazyZone.Value;

    /// <summary>The instant expressed with the Tunis offset.</summary>
    public static DateTimeOffset ToTunis(DateTimeOffset instant) => TimeZoneInfo.ConvertTime(instant, Zone);

    /// <summary>Current time in Tunis.</summary>
    public static DateTimeOffset Now(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        return ToTunis(timeProvider.GetUtcNow());
    }

    /// <summary>Current calendar date in Tunis.</summary>
    public static DateOnly Today(TimeProvider timeProvider) => DateOnly.FromDateTime(Now(timeProvider).DateTime);

    /// <summary>The Tunis calendar date of an instant.</summary>
    public static DateOnly DateOf(DateTimeOffset instant) => DateOnly.FromDateTime(ToTunis(instant).DateTime);

    /// <summary>
    /// Interprets a wall-clock date and time in Tunis and returns the corresponding instant (UTC).
    /// The <see cref="DateTime.Kind"/> of <paramref name="localDateTime"/> is ignored.
    /// </summary>
    /// <remarks>
    /// Tunisia observes no daylight saving time today; for historical dates with DST, a time in the spring gap is moved
    /// forward past the gap and an ambiguous autumn time resolves to its first occurrence.
    /// </remarks>
    public static DateTimeOffset FromLocal(DateTime localDateTime)
    {
        var local = DateTime.SpecifyKind(localDateTime, DateTimeKind.Unspecified);
        var zone = Zone;

        while (zone.IsInvalidTime(local))
        {
            local = local.AddMinutes(1);
        }

        var offset = zone.IsAmbiguousTime(local)
            ? zone.GetAmbiguousTimeOffsets(local).Max()
            : zone.GetUtcOffset(local);

        return new DateTimeOffset(local, offset).ToUniversalTime();
    }

    /// <summary>Instant (UTC) of a Tunis wall-clock <paramref name="date"/> and <paramref name="time"/>.</summary>
    public static DateTimeOffset FromLocal(DateOnly date, TimeOnly time) => FromLocal(date.ToDateTime(time));

    /// <summary>Instant (UTC) of midnight in Tunis at the start of <paramref name="date"/>.</summary>
    public static DateTimeOffset StartOfDay(DateOnly date) => FromLocal(date.ToDateTime(TimeOnly.MinValue));

    private static TimeZoneInfo ResolveZone()
    {
        if (TimeZoneInfo.TryFindSystemTimeZoneById(TimeZoneId, out var zone))
        {
            return zone;
        }

        if (TimeZoneInfo.TryConvertIanaIdToWindowsId(TimeZoneId, out var windowsId)
            && TimeZoneInfo.TryFindSystemTimeZoneById(windowsId, out zone))
        {
            return zone;
        }

        throw new TimeZoneNotFoundException($"The time zone '{TimeZoneId}' is not available on this system.");
    }
}
