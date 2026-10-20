using CarRental.Domain.Bookings.Availability;
using CarRental.Domain.Common;
using static CarRental.Domain.Tests.Bookings.Availability.AvailabilityTestData;

namespace CarRental.Domain.Tests.Bookings.Availability;

public sealed class StepFunctionTests
{
    [Fact]
    public void Zero_is_zero_everywhere()
    {
        StepFunction.Zero.ValueAt(At(0)).ShouldBe(0);
        StepFunction.Zero.BreakpointCount.ShouldBe(0);
        StepFunction.Zero.Segments(P(0, 10)).ShouldBe([new StepSegment(P(0, 10), 0)]);
        StepFunction.CountOf([]).ShouldBe(StepFunction.Zero);
    }

    [Fact]
    public void CountOf_counts_the_periods_containing_each_instant()
    {
        var f = StepFunction.CountOf([P(0, 10), P(5, 15), P(20, 30)]);

        f.ValueAt(At(-1)).ShouldBe(0);
        f.ValueAt(At(0)).ShouldBe(1);
        f.ValueAt(At(5)).ShouldBe(2);
        f.ValueAt(At(9.99)).ShouldBe(2);
        f.ValueAt(At(10)).ShouldBe(1);
        f.ValueAt(At(15)).ShouldBe(0);
        f.ValueAt(At(20)).ShouldBe(1);
        f.ValueAt(At(30)).ShouldBe(0);
        f.ValueAt(At(1000)).ShouldBe(0);
    }

    [Fact]
    public void Touching_periods_do_not_overlap_and_produce_no_breakpoint_at_the_junction()
    {
        var f = StepFunction.CountOf([P(0, 10), P(10, 20)]);

        f.ValueAt(At(10)).ShouldBe(1);
        f.Breakpoints.ShouldBe([new StepBreakpoint(At(0), 1), new StepBreakpoint(At(20), 0)]);
    }

    [Fact]
    public void Sum_weights_the_periods_and_ignores_zero_weights()
    {
        var f = StepFunction.Sum([(P(0, 10), 2), (P(5, 8), -1), (P(1, 2), 0)]);

        f.ValueAt(At(0)).ShouldBe(2);
        f.ValueAt(At(6)).ShouldBe(1);
        f.ValueAt(At(8)).ShouldBe(2);
        f.ValueAt(At(10)).ShouldBe(0);
        f.BreakpointCount.ShouldBe(4);
    }

    [Fact]
    public void Segments_cover_the_window_exactly_with_maximal_constant_pieces()
    {
        var f = StepFunction.CountOf([P(0, 10), P(5, 15)]);

        f.Segments(P(-5, 12)).ShouldBe(
        [
            new StepSegment(P(-5, 0), 0),
            new StepSegment(P(0, 5), 1),
            new StepSegment(P(5, 10), 2),
            new StepSegment(P(10, 12), 1),
        ]);
        f.Segments(P(6, 7)).ShouldBe([new StepSegment(P(6, 7), 2)]);
        f.Segments(P(15, 20)).ShouldBe([new StepSegment(P(15, 20), 0)]);
    }

    [Fact]
    public void Minimum_and_maximum_are_taken_over_the_window()
    {
        var f = StepFunction.Sum([(P(0, 10), 3), (P(4, 6), -2)]);

        f.Minimum(P(0, 10)).ShouldBe(1);
        f.Maximum(P(0, 10)).ShouldBe(3);
        f.Minimum(P(-1, 1)).ShouldBe(0);
        f.Minimum(P(6, 8)).ShouldBe(3);
    }

    [Fact]
    public void Combine_applies_the_operation_pointwise_in_canonical_form()
    {
        var a = StepFunction.CountOf([P(0, 10)]);
        var b = StepFunction.CountOf([P(5, 15)]);

        var sum = a.Plus(b);
        var difference = a.Minus(b);

        sum.Segments(P(0, 15)).ShouldBe(
        [
            new StepSegment(P(0, 5), 1),
            new StepSegment(P(5, 10), 2),
            new StepSegment(P(10, 15), 1),
        ]);
        difference.ValueAt(At(12)).ShouldBe(-1);
        a.Minus(a).ShouldBe(StepFunction.Zero);
        StepFunction.Combine(a, b, Math.Max).ShouldBe(StepFunction.CountOf([P(0, 15)]));
    }

    [Fact]
    public void Combine_requires_zero_to_combine_into_zero()
    {
        var a = StepFunction.CountOf([P(0, 10)]);

        Should.Throw<ArgumentException>(() => StepFunction.Combine(a, a, (x, y) => x + y + 1));
    }

    [Fact]
    public void Equality_compares_the_functions()
    {
        StepFunction.CountOf([P(0, 5), P(5, 10)]).ShouldBe(StepFunction.CountOf([P(0, 10)]));
        StepFunction.CountOf([P(0, 5)]).GetHashCode().ShouldBe(StepFunction.CountOf([P(0, 5)]).GetHashCode());
        StepFunction.CountOf([P(0, 5)]).Equals(StepFunction.CountOf([P(0, 6)])).ShouldBeFalse();
        StepFunction.CountOf([P(0, 5)]).Equals((object?)null).ShouldBeFalse();
        StepFunction.CountOf([P(0, 5)]).ToString().ShouldBe("0 | 2027-07-01T08:00:00Z: 1 | 2027-07-01T13:00:00Z: 0");
    }

    [Fact]
    public void Instants_with_another_offset_are_the_same_instants()
    {
        var shifted = Period.Create(At(0).ToOffset(TimeSpan.FromHours(1)), At(10).ToOffset(TimeSpan.FromHours(1))).Value;

        StepFunction.CountOf([shifted]).ShouldBe(StepFunction.CountOf([P(0, 10)]));
        StepFunction.CountOf([P(0, 10)]).ValueAt(At(9).ToOffset(TimeSpan.FromHours(-5))).ShouldBe(1);
    }

    [Fact]
    public void Null_arguments_are_bugs()
    {
        Should.Throw<ArgumentNullException>(() => StepFunction.CountOf(null!));
        Should.Throw<ArgumentNullException>(() => StepFunction.Sum(null!));
        Should.Throw<ArgumentNullException>(() => StepFunction.CountOf([null!]));
        Should.Throw<ArgumentNullException>(() => StepFunction.Zero.Segments(null!));
        Should.Throw<ArgumentNullException>(() => StepFunction.Combine(null!, StepFunction.Zero, Math.Max));
        Should.Throw<ArgumentNullException>(() => StepFunction.Combine(StepFunction.Zero, StepFunction.Zero, null!));
    }
}
