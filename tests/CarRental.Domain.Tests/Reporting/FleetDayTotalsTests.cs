using CarRental.Domain.Common;
using CarRental.Domain.Reporting;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;

namespace CarRental.Domain.Tests.Reporting;

/// <summary>The fleet indicators of SPEC §8 computed from vehicle-days (ADR-0046).</summary>
public sealed class FleetDayTotalsTests
{
    private static readonly Guid Vehicle = Guid.CreateVersion7();

    [Fact]
    public void The_indicators_follow_the_definitions_of_the_specification()
    {
        var days = new[]
        {
            Day(available: true, rented: true, revenue: 120m, commission: 18m, costs: 5m, depreciation: 20m),
            Day(available: true, rented: true, revenue: 100m, commission: 0m, costs: 0m, depreciation: 20m),
            Day(available: true, rented: false, revenue: 0m, commission: 0m, costs: 0m, depreciation: 20m),
            Day(available: false, rented: false, revenue: 0m, commission: 0m, costs: 40m, depreciation: 20m),
        };

        var totals = FleetDayTotals.Of(days);

        totals.VehicleDays.ShouldBe(4);
        totals.AvailableDays.ShouldBe(3);
        totals.RentedDays.ShouldBe(2);
        totals.Revenue.ShouldBe(Money.Tnd(220m));
        totals.NetRevenue.ShouldBe(Money.Tnd(202m));
        totals.Costs.ShouldBe(Money.Tnd(125m));
        totals.Margin.ShouldBe(Money.Tnd(77m));
        totals.RevenuePerAvailableDay.ShouldBe(Money.Tnd(73.333m));
        totals.Utilization!.Value.ShouldBe(2m / 3m);
        totals.AverageRentedDayPrice.ShouldBe(Money.Tnd(110m));
        totals.MarginPerAvailableDay.ShouldBe(Money.Tnd(25.667m));
    }

    [Fact]
    public void Without_available_or_rented_days_the_ratios_have_no_value()
    {
        var totals = FleetDayTotals.Of([Day(available: false, rented: false, revenue: 0m, commission: 0m, costs: 0m, depreciation: 10m)]);

        totals.RevenuePerAvailableDay.ShouldBeNull();
        totals.Utilization.ShouldBeNull();
        totals.AverageRentedDayPrice.ShouldBeNull();
        totals.Margin.ShouldBe(Money.Tnd(-10m));
        FleetDayTotals.Empty.Utilization.ShouldBeNull();
    }

    [Fact]
    public void Totals_add_up()
    {
        var first = FleetDayTotals.Of([Day(true, true, 100m, 10m, 1m, 2m)]);
        var second = FleetDayTotals.Of([Day(true, false, 0m, 0m, 3m, 2m), Day(false, false, 0m, 0m, 0m, 2m)]);

        first.Add(second).ShouldBe(FleetDayTotals.Of([Day(true, true, 100m, 10m, 1m, 2m), Day(true, false, 0m, 0m, 3m, 2m), Day(false, false, 0m, 0m, 0m, 2m)]));
    }

    [Property(MaxTest = 1000)]
    public Property Revenue_per_available_day_is_utilization_times_the_average_price_per_rented_day()
    {
        return Prop.ForAll(Gen.Choose(1, 60).ToArbitrary(), Gen.Choose(0, 60).ToArbitrary(), Gen.Choose(0, 5_000_000).ToArbitrary(), (available, rented, millimes) =>
        {
            var rentedDays = Math.Min(rented, available);
            var revenue = rentedDays == 0 ? 0m : millimes / 1000m;
            var totals = new FleetDayTotals(available, available, rentedDays, Money.Tnd(revenue), Money.Zero, Money.Zero, Money.Zero);
            var product = totals.AverageRentedDayPrice is { } price ? price.Amount * totals.Utilization!.Value : 0m;

            // Both sides are rounded to the millime: they agree within one millime.
            return Math.Abs(totals.RevenuePerAvailableDay!.Value.Amount - product) <= 0.001m;
        });
    }

    private static VehicleDayFigures Day(bool available, bool rented, decimal revenue, decimal commission, decimal costs, decimal depreciation) =>
        new(Vehicle, new DateOnly(2026, 9, 1), available, rented, Money.Tnd(revenue), Money.Tnd(commission), Money.Tnd(costs), Money.Tnd(depreciation));
}
