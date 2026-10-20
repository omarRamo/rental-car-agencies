using System.Globalization;
using System.Text;
using CarRental.Domain.Common;

namespace CarRental.Domain.Bookings.Availability;

/// <summary>
/// An integer-valued step function of time ("fonction en escalier"): 0 before the first breakpoint, then the value of
/// the last breakpoint at or before the instant. Capacity and occupancy of a category are step functions that only
/// change at the bounds of vehicle windows and occupancy intervals, so they are evaluated by sweeping those bounds.
/// </summary>
/// <remarks>
/// Immutable and kept in canonical form: breakpoints are strictly increasing UTC instants and each one changes the
/// value, so two equal functions have the same breakpoints. Building from n periods is O(n log n); a value lookup is
/// O(log n); a window of k breakpoints is read in O(log n + k).
/// </remarks>
public sealed class StepFunction : IEquatable<StepFunction>
{
    private readonly long[] _ticks;
    private readonly int[] _values;

    private StepFunction(long[] ticks, int[] values)
    {
        _ticks = ticks;
        _values = values;
    }

    /// <summary>The function equal to 0 everywhere.</summary>
    public static StepFunction Zero { get; } = new([], []);

    /// <summary>Number of instants where the value changes.</summary>
    public int BreakpointCount => _ticks.Length;

    /// <summary>The instants where the value changes, in order, with the value from each instant on.</summary>
    public IReadOnlyList<StepBreakpoint> Breakpoints =>
        [.. _ticks.Select((ticks, index) => new StepBreakpoint(ToInstant(ticks), _values[index]))];

    /// <summary>Number of <paramref name="periods"/> containing each instant (half-open periods).</summary>
    public static StepFunction CountOf(IEnumerable<Period> periods)
    {
        ArgumentNullException.ThrowIfNull(periods);
        return Sum(periods.Select(period => (Period: period, Weight: 1)));
    }

    /// <summary>Sum of the weighted indicator functions of the periods: <c>f(t) = Σ weight × [t ∈ period]</c>.</summary>
    public static StepFunction Sum(IEnumerable<(Period Period, int Weight)> terms)
    {
        ArgumentNullException.ThrowIfNull(terms);

        var instants = new List<long>();
        var deltas = new List<int>();
        foreach (var (period, weight) in terms)
        {
            ArgumentNullException.ThrowIfNull(period, nameof(terms));
            if (weight == 0)
            {
                continue;
            }

            instants.Add(period.Start.UtcTicks);
            deltas.Add(weight);
            instants.Add(period.End.UtcTicks);
            deltas.Add(-weight);
        }

        return FromDeltas([.. instants], [.. deltas]);
    }

    /// <summary>
    /// The pointwise combination <c>combine(left(t), right(t))</c>, computed by merging both breakpoint lists in
    /// O(n + m). <paramref name="combine"/> must map (0, 0) to 0, the value of both functions before any breakpoint.
    /// </summary>
    public static StepFunction Combine(StepFunction left, StepFunction right, Func<int, int, int> combine)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        ArgumentNullException.ThrowIfNull(combine);
        if (combine(0, 0) != 0)
        {
            throw new ArgumentException("Combining two zero values must give zero.", nameof(combine));
        }

        var ticks = new List<long>(left._ticks.Length + right._ticks.Length);
        var values = new List<int>(left._ticks.Length + right._ticks.Length);
        int i = 0, j = 0, leftValue = 0, rightValue = 0, previous = 0;
        while (i < left._ticks.Length || j < right._ticks.Length)
        {
            long at;
            if (j >= right._ticks.Length || (i < left._ticks.Length && left._ticks[i] < right._ticks[j]))
            {
                at = left._ticks[i];
                leftValue = left._values[i++];
            }
            else if (i >= left._ticks.Length || right._ticks[j] < left._ticks[i])
            {
                at = right._ticks[j];
                rightValue = right._values[j++];
            }
            else
            {
                at = left._ticks[i];
                leftValue = left._values[i++];
                rightValue = right._values[j++];
            }

            var value = combine(leftValue, rightValue);
            if (value != previous)
            {
                ticks.Add(at);
                values.Add(value);
                previous = value;
            }
        }

        return ticks.Count == 0 ? Zero : new StepFunction([.. ticks], [.. values]);
    }

    public StepFunction Plus(StepFunction other) => Combine(this, other, static (a, b) => a + b);

    public StepFunction Minus(StepFunction other) => Combine(this, other, static (a, b) => a - b);

    public int ValueAt(DateTimeOffset instant)
    {
        var index = IndexAtOrBefore(instant.UtcTicks);
        return index < 0 ? 0 : _values[index];
    }

    /// <summary>The maximal constant pieces of the function within <paramref name="window"/>, in order, covering it exactly.</summary>
    public IReadOnlyList<StepSegment> Segments(Period window)
    {
        ArgumentNullException.ThrowIfNull(window);

        var segments = new List<StepSegment>();
        var end = window.End.UtcTicks;
        var cursor = new StepCursor(this, window.Start.UtcTicks);
        for (var at = window.Start.UtcTicks; at < end;)
        {
            var next = Math.Min(cursor.NextChange, end);
            segments.Add(new StepSegment(PeriodBetween(at, next), cursor.Value));
            at = next;
            cursor.MoveTo(at);
        }

        return segments;
    }

    /// <summary>The lowest value taken within <paramref name="window"/>.</summary>
    public int Minimum(Period window) => Segments(window).Min(segment => segment.Value);

    /// <summary>The highest value taken within <paramref name="window"/>.</summary>
    public int Maximum(Period window) => Segments(window).Max(segment => segment.Value);

    public bool Equals(StepFunction? other) =>
        other is not null && _ticks.AsSpan().SequenceEqual(other._ticks) && _values.AsSpan().SequenceEqual(other._values);

    public override bool Equals(object? obj) => obj is StepFunction other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        for (var i = 0; i < _ticks.Length; i++)
        {
            hash.Add(_ticks[i]);
            hash.Add(_values[i]);
        }

        return hash.ToHashCode();
    }

    /// <summary>Diagnostic text, e.g. <c>0 | 2027-07-01T08:00:00Z: 1 | 2027-07-01T13:00:00Z: 0</c>.</summary>
    public override string ToString()
    {
        var text = new StringBuilder("0");
        for (var i = 0; i < _ticks.Length; i++)
        {
            text.Append(CultureInfo.InvariantCulture, $" | {ToInstant(_ticks[i]):yyyy-MM-ddTHH:mm:ssZ}: {_values[i]}");
        }

        return text.ToString();
    }

    internal static DateTimeOffset ToInstant(long utcTicks) => new(utcTicks, TimeSpan.Zero);

    internal static Period PeriodBetween(long startTicks, long endTicks) =>
        Period.Create(ToInstant(startTicks), ToInstant(endTicks)).Value;

    internal int Count => _ticks.Length;

    internal long TicksAt(int index) => _ticks[index];

    internal int ValueAtIndex(int index) => _values[index];

    /// <summary>Index of the last breakpoint at or before <paramref name="utcTicks"/>, or -1 when there is none.</summary>
    internal int IndexAtOrBefore(long utcTicks)
    {
        var index = Array.BinarySearch(_ticks, utcTicks);
        return index >= 0 ? index : ~index - 1;
    }

    private static StepFunction FromDeltas(long[] instants, int[] deltas)
    {
        if (instants.Length == 0)
        {
            return Zero;
        }

        Array.Sort(instants, deltas);
        var ticks = new List<long>(instants.Length);
        var values = new List<int>(instants.Length);
        var current = 0;
        var i = 0;
        while (i < instants.Length)
        {
            var at = instants[i];
            var change = 0;
            while (i < instants.Length && instants[i] == at)
            {
                change += deltas[i++];
            }

            if (change != 0)
            {
                current += change;
                ticks.Add(at);
                values.Add(current);
            }
        }

        return ticks.Count == 0 ? Zero : new StepFunction([.. ticks], [.. values]);
    }
}

/// <summary>A constant piece of a <see cref="StepFunction"/>.</summary>
public readonly record struct StepSegment(Period Period, int Value);

/// <summary>An instant where a <see cref="StepFunction"/> changes, with its value from that instant on.</summary>
public readonly record struct StepBreakpoint(DateTimeOffset At, int Value);

/// <summary>
/// Forward-only reader of a <see cref="StepFunction"/>: the value at the current position and the next instant where
/// it changes. A mutable struct for allocation-free sweeps: keep it in a local variable.
/// </summary>
internal struct StepCursor
{
    private readonly StepFunction _function;
    private int _next;

    public StepCursor(StepFunction function, long startTicks)
    {
        _function = function;
        var index = function.IndexAtOrBefore(startTicks);
        Value = index < 0 ? 0 : function.ValueAtIndex(index);
        _next = index + 1;
        NextChange = _next < function.Count ? function.TicksAt(_next) : long.MaxValue;
    }

    /// <summary>Value at the current position.</summary>
    public int Value { get; private set; }

    /// <summary>Next instant (UTC ticks) where the value changes, <see cref="long.MaxValue"/> when it never does.</summary>
    public long NextChange { get; private set; }

    /// <summary>Moves forward to <paramref name="utcTicks"/>.</summary>
    public void MoveTo(long utcTicks)
    {
        while (NextChange <= utcTicks)
        {
            Value = _function.ValueAtIndex(_next++);
            NextChange = _next < _function.Count ? _function.TicksAt(_next) : long.MaxValue;
        }
    }
}
