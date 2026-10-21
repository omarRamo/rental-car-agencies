using CarRental.Domain.Common;

namespace CarRental.Integration.Tests.Scenarios;

/// <summary>
/// The clock of a scenario host: the real clock shifted by an offset that the scenario moves forward, never back (the
/// time of a high-season day goes from the opening to the closing). Only the wall-clock time is shifted: timers and
/// timestamps stay real, so that timeouts, delays and the hosted services behave as usual.
/// </summary>
public sealed class ScenarioClock : TimeProvider
{
    private long _offsetTicks;

    public override DateTimeOffset GetUtcNow() => base.GetUtcNow().AddTicks(Interlocked.Read(ref _offsetTicks));

    /// <summary>The current Tunis day of the scenario.</summary>
    public DateOnly Today => TunisTime.Today(this);

    /// <summary>Moves the clock to <paramref name="instant"/>, which must not be in the past of the scenario.</summary>
    public void JumpTo(DateTimeOffset instant)
    {
        var now = GetUtcNow();
        if (instant < now)
        {
            throw new InvalidOperationException($"The scenario clock only moves forward: {instant:O} is before {now:O}.");
        }

        Interlocked.Exchange(ref _offsetTicks, (instant - base.GetUtcNow()).Ticks);
    }

    /// <summary>Moves the clock to <paramref name="hour"/>:<paramref name="minute"/> (Tunis time) of <paramref name="day"/>.</summary>
    public DateTimeOffset JumpTo(DateOnly day, int hour, int minute = 0)
    {
        var instant = TunisTime.FromLocal(day, new TimeOnly(hour, minute));
        JumpTo(instant);
        return instant;
    }
}
