using System.Globalization;

namespace CarRental.Domain.Common;

/// <summary>
/// A half-open time interval <c>[Start, End)</c> between two instants, stored in UTC.
/// <see cref="End"/> is excluded: a rental returned at 10:00 and another one starting at 10:00 do not overlap.
/// </summary>
/// <remarks>Business rules on calendar days are evaluated in Africa/Tunis with <see cref="TunisTime"/>.</remarks>
public sealed class Period : IEquatable<Period>
{
    private Period(DateTimeOffset start, DateTimeOffset end)
    {
        Start = start;
        End = end;
    }

    /// <summary>First instant included, UTC.</summary>
    public DateTimeOffset Start { get; }

    /// <summary>First instant excluded, UTC.</summary>
    public DateTimeOffset End { get; }

    public TimeSpan Duration => End - Start;

    public static bool operator ==(Period? left, Period? right) => Equals(left, right);

    public static bool operator !=(Period? left, Period? right) => !Equals(left, right);

    /// <summary>Creates a period; <paramref name="end"/> must be strictly after <paramref name="start"/>. Both are normalized to UTC.</summary>
    public static Result<Period> Create(DateTimeOffset start, DateTimeOffset end)
    {
        if (end <= start)
        {
            return PeriodErrors.EndMustBeAfterStart;
        }

        return new Period(start.ToUniversalTime(), end.ToUniversalTime());
    }

    public static Result<Period> FromDuration(DateTimeOffset start, TimeSpan duration) =>
        duration <= TimeSpan.Zero ? PeriodErrors.EndMustBeAfterStart : Create(start, start + duration);

    /// <summary>True when <paramref name="instant"/> is in <c>[Start, End)</c>.</summary>
    public bool Contains(DateTimeOffset instant) => instant >= Start && instant < End;

    /// <summary>True when <paramref name="other"/> lies entirely within this period.</summary>
    public bool Contains(Period other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return other.Start >= Start && other.End <= End;
    }

    /// <summary>True when both periods share at least one instant (touching periods do not overlap).</summary>
    public bool Overlaps(Period other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return Start < other.End && other.Start < End;
    }

    /// <summary>The common part of both periods, or <see langword="null"/> when they do not overlap.</summary>
    public Period? Intersect(Period other)
    {
        ArgumentNullException.ThrowIfNull(other);
        var start = Start > other.Start ? Start : other.Start;
        var end = End < other.End ? End : other.End;
        return end > start ? new Period(start, end) : null;
    }

    /// <summary>Extends the end, e.g. to add the preparation buffer after a return: <c>[pickup, return + buffer)</c>.</summary>
    public Period ExtendEnd(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration), duration, "The extension cannot be negative.");
        }

        return new Period(Start, End + duration);
    }

    public bool Equals(Period? other) => other is not null && Start == other.Start && End == other.End;

    public override bool Equals(object? obj) => obj is Period other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Start, End);

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"[{Start:yyyy-MM-ddTHH:mm:ssZ}, {End:yyyy-MM-ddTHH:mm:ssZ})");
}
