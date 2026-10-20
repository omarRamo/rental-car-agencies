using CarRental.Domain.Common;

namespace CarRental.Domain.Tests.Common;

public sealed class PeriodTests
{
    private static readonly DateTimeOffset T0 = new(2027, 6, 28, 9, 0, 0, TimeSpan.Zero);

    private static Period P(int startHours, int endHours) => Period.Create(T0.AddHours(startHours), T0.AddHours(endHours)).Value;

    [Fact]
    public void Create_rejects_an_empty_or_reversed_period()
    {
        Period.Create(T0, T0).Error.ShouldBe(PeriodErrors.EndMustBeAfterStart);
        Period.Create(T0, T0.AddTicks(-1)).Error.ShouldBe(PeriodErrors.EndMustBeAfterStart);
        Period.FromDuration(T0, TimeSpan.Zero).IsFailure.ShouldBeTrue();
        Period.FromDuration(T0, TimeSpan.FromMinutes(-1)).IsFailure.ShouldBeTrue();
    }

    [Fact]
    public void Create_normalizes_to_utc_without_changing_the_instant()
    {
        var tunisPickup = new DateTimeOffset(2027, 6, 28, 10, 0, 0, TimeSpan.FromHours(1));
        var period = Period.Create(tunisPickup, tunisPickup.AddDays(5)).Value;

        period.Start.Offset.ShouldBe(TimeSpan.Zero);
        period.Start.ShouldBe(tunisPickup);
        period.Start.UtcDateTime.Hour.ShouldBe(9);
        period.Duration.ShouldBe(TimeSpan.FromDays(5));
    }

    [Fact]
    public void FromDuration_builds_the_end()
    {
        var period = Period.FromDuration(T0, TimeSpan.FromHours(2)).Value;

        period.End.ShouldBe(T0.AddHours(2));
    }

    [Fact]
    public void Contains_includes_the_start_and_excludes_the_end()
    {
        var period = P(0, 24);

        period.Contains(T0).ShouldBeTrue();
        period.Contains(T0.AddHours(12)).ShouldBeTrue();
        period.Contains(T0.AddHours(24).AddTicks(-1)).ShouldBeTrue();
        period.Contains(T0.AddHours(24)).ShouldBeFalse();
        period.Contains(T0.AddTicks(-1)).ShouldBeFalse();
    }

    [Theory]
    [InlineData(0, 10, 10, 20, false)] // touching: back-to-back rentals
    [InlineData(10, 20, 0, 10, false)]
    [InlineData(0, 10, 9, 20, true)]
    [InlineData(0, 10, 2, 5, true)] // inside
    [InlineData(2, 5, 0, 10, true)] // around
    [InlineData(0, 10, 0, 10, true)] // identical
    [InlineData(0, 10, 11, 20, false)]
    public void Overlaps_uses_half_open_intervals(int s1, int e1, int s2, int e2, bool expected)
    {
        P(s1, e1).Overlaps(P(s2, e2)).ShouldBe(expected);
        P(s2, e2).Overlaps(P(s1, e1)).ShouldBe(expected);
    }

    [Fact]
    public void Contains_period_checks_both_bounds()
    {
        P(0, 10).Contains(P(0, 10)).ShouldBeTrue();
        P(0, 10).Contains(P(2, 8)).ShouldBeTrue();
        P(0, 10).Contains(P(2, 11)).ShouldBeFalse();
        P(2, 10).Contains(P(1, 5)).ShouldBeFalse();
    }

    [Fact]
    public void Intersect_returns_the_common_part_or_null()
    {
        P(0, 10).Intersect(P(5, 20)).ShouldBe(P(5, 10));
        P(0, 10).Intersect(P(2, 3)).ShouldBe(P(2, 3));
        P(0, 10).Intersect(P(10, 20)).ShouldBeNull();
        P(0, 10).Intersect(P(12, 20)).ShouldBeNull();
    }

    [Fact]
    public void ExtendEnd_adds_the_preparation_buffer()
    {
        var rental = P(0, 120);
        var withBuffer = rental.ExtendEnd(TimeSpan.FromHours(2));

        withBuffer.ShouldBe(P(0, 122));
        rental.Overlaps(P(121, 130)).ShouldBeFalse();
        withBuffer.Overlaps(P(121, 130)).ShouldBeTrue();
        Should.Throw<ArgumentOutOfRangeException>(() => rental.ExtendEnd(TimeSpan.FromMinutes(-1)));
    }

    [Fact]
    public void Equality_compares_instants()
    {
        var utc = Period.Create(T0, T0.AddHours(1)).Value;
        var shifted = Period.Create(T0.ToOffset(TimeSpan.FromHours(1)), T0.AddHours(1).ToOffset(TimeSpan.FromHours(1))).Value;

        (utc == shifted).ShouldBeTrue();
        utc.GetHashCode().ShouldBe(shifted.GetHashCode());
        (utc != P(0, 2)).ShouldBeTrue();
        utc.Equals((object)shifted).ShouldBeTrue();
        utc.Equals(null).ShouldBeFalse();
    }

    [Fact]
    public void ToString_is_an_iso_half_open_interval()
    {
        P(0, 1).ToString().ShouldBe("[2027-06-28T09:00:00Z, 2027-06-28T10:00:00Z)");
    }

    [Fact]
    public void Null_arguments_are_bugs()
    {
        var period = P(0, 1);

        Should.Throw<ArgumentNullException>(() => period.Overlaps(null!));
        Should.Throw<ArgumentNullException>(() => period.Contains((Period)null!));
        Should.Throw<ArgumentNullException>(() => period.Intersect(null!));
    }
}
