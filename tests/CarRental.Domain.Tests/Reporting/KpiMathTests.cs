using CarRental.Domain.Common;
using CarRental.Domain.Reporting;

namespace CarRental.Domain.Tests.Reporting;

/// <summary>Ratios, amounts per unit, changes and medians of the indicators (ADR-0046).</summary>
public sealed class KpiMathTests
{
    [Fact]
    public void A_ratio_or_an_amount_per_unit_has_no_value_without_a_denominator()
    {
        KpiMath.Ratio(5m, 0m).ShouldBeNull();
        KpiMath.Ratio(0m, 10m).ShouldBe(0m);
        KpiMath.Ratio(5m, 10m).ShouldBe(0.5m);
        KpiMath.PerUnit(Money.Tnd(100m), 0m).ShouldBeNull();
    }

    [Fact]
    public void An_amount_per_unit_is_rounded_to_the_millime()
    {
        KpiMath.PerUnit(Money.Tnd(100m), 3m).ShouldBe(Money.Tnd(33.333m));
        KpiMath.PerUnit(Money.Tnd(200m), 3m).ShouldBe(Money.Tnd(66.667m));
    }

    [Fact]
    public void A_change_is_relative_to_the_previous_value_and_undefined_from_zero()
    {
        KpiMath.Change(120m, 100m).ShouldBe(0.2m);
        KpiMath.Change(80m, 100m).ShouldBe(-0.2m);
        KpiMath.Change(-50m, -100m).ShouldBe(0.5m);
        KpiMath.Change(10m, 0m).ShouldBeNull();
        KpiMath.Change(null, 10m).ShouldBeNull();
        KpiMath.Change(Money.Tnd(150m), Money.Tnd(100m)).ShouldBe(0.5m);
        KpiMath.Change((Money?)null, Money.Tnd(100m)).ShouldBeNull();
    }

    [Fact]
    public void The_median_is_the_middle_value_or_the_mean_of_the_two_middle_values()
    {
        KpiMath.Median([]).ShouldBeNull();
        KpiMath.Median([TimeSpan.FromMinutes(30)]).ShouldBe(TimeSpan.FromMinutes(30));
        KpiMath.Median([TimeSpan.FromHours(5), TimeSpan.FromMinutes(10), TimeSpan.FromHours(1)]).ShouldBe(TimeSpan.FromHours(1));
        KpiMath.Median([TimeSpan.FromHours(4), TimeSpan.FromHours(1), TimeSpan.FromHours(2), TimeSpan.FromHours(3)])
            .ShouldBe(TimeSpan.FromMinutes(150));
    }
}
