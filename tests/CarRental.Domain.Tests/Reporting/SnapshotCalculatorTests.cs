using CarRental.Domain.Common;
using CarRental.Domain.Reporting;

namespace CarRental.Domain.Tests.Reporting;

/// <summary>The daily snapshots of a range of days (ADR-0045), computed from fleet days, counted rentals and costs.</summary>
public sealed class SnapshotCalculatorTests
{
    private static readonly Guid Agency = Guid.CreateVersion7();
    private static readonly Guid Category = Guid.CreateVersion7();
    private static readonly DateOnly First = new(2026, 9, 1);
    private static readonly DateTimeOffset ComputedAt = new(2026, 10, 1, 2, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_vehicle_rented_five_of_ten_available_days_has_five_rented_rows_carrying_the_whole_revenue()
    {
        var vehicle = Guid.CreateVersion7();
        var rental = Rental(vehicle, First.AddDays(2), days: 5, revenue: 500m);

        var result = Compute(First, First.AddDays(9), Fleet(vehicle, First, 10), [rental]);

        result.Rows.Count.ShouldBe(10);
        result.Rows.Count(r => r.IsAvailable).ShouldBe(10);
        result.Rows.Where(r => r.IsRented).Select(r => r.Day).ShouldBe(Enumerable.Range(2, 5).Select(i => First.AddDays(i)));
        result.Rows.Where(r => r.IsRented).ShouldAllBe(r => r.RentalRevenue == Money.Tnd(100m) && r.BookingId == rental.BookingId);
        result.Rows.Select(r => r.RentalRevenue).Sum().ShouldBe(Money.Tnd(500m));
        result.UnattributedRevenue.ShouldBe(Money.Zero);
        var totals = FleetDayTotals.Of(result.Rows.Select(Figures));
        totals.Utilization.ShouldBe(0.5m);
        totals.RevenuePerAvailableDay.ShouldBe(Money.Tnd(50m));
        totals.AverageRentedDayPrice.ShouldBe(Money.Tnd(100m));
    }

    [Fact]
    public void The_rounding_remainder_of_the_revenue_goes_to_the_last_rented_day()
    {
        var vehicle = Guid.CreateVersion7();
        var rental = Rental(vehicle, First, days: 3, revenue: 100m);

        var result = Compute(First, First.AddDays(4), Fleet(vehicle, First, 5), [rental]);

        result.Rows.Where(r => r.IsRented).Select(r => r.RentalRevenue.Amount).ShouldBe([33.333m, 33.333m, 33.334m]);
    }

    [Fact]
    public void Computing_a_range_in_two_parts_gives_the_same_shares_and_the_whole_revenue()
    {
        var vehicle = Guid.CreateVersion7();
        var fleet = Fleet(vehicle, First, 10);
        var rental = Rental(vehicle, First.AddDays(2), days: 5, revenue: 100.001m, commission: 15.002m);

        var whole = Compute(First, First.AddDays(9), fleet, [rental]);
        var firstPart = Compute(First, First.AddDays(3), fleet, [rental]);
        var secondPart = Compute(First.AddDays(4), First.AddDays(9), fleet, [rental]);

        var parts = firstPart.Rows.Concat(secondPart.Rows).ToList();
        parts.Select(r => (r.Day, r.RentalRevenue, r.Commission)).ShouldBe(whole.Rows.Select(r => (r.Day, r.RentalRevenue, r.Commission)));
        parts.Select(r => r.RentalRevenue).Sum().ShouldBe(Money.Tnd(100.001m));
        parts.Select(r => r.Commission).Sum().ShouldBe(Money.Tnd(15.002m));
    }

    [Fact]
    public void After_a_vehicle_swap_the_following_rented_days_belong_to_the_replacement_vehicle()
    {
        var first = Guid.CreateVersion7();
        var replacement = Guid.CreateVersion7();
        var pickup = At(First, 10);
        var swappedAt = pickup.AddDays(1).AddHours(5);
        var rental = Rental(first, First, days: 4, revenue: 400m) with
        {
            Vehicles = [new CarrierSpan(first, null), new CarrierSpan(replacement, swappedAt)],
        };

        var result = Compute(First, First.AddDays(4), [.. Fleet(first, First, 5), .. Fleet(replacement, First, 5, plate: "900 TU 1")], [rental]);

        // Units start at 10:00: the second one (day 2) started before the swap at 15:00, the third one after it.
        result.Rows.Where(r => r.VehicleId == first && r.IsRented).Select(r => r.Day).ShouldBe([First, First.AddDays(1)]);
        result.Rows.Where(r => r.VehicleId == replacement && r.IsRented).Select(r => r.Day).ShouldBe([First.AddDays(2), First.AddDays(3)]);
        result.Rows.Select(r => r.RentalRevenue).Sum().ShouldBe(Money.Tnd(400m));
    }

    [Fact]
    public void A_day_shared_by_two_rentals_keeps_both_shares_and_names_the_larger_one()
    {
        var vehicle = Guid.CreateVersion7();
        var late = Rental(vehicle, First, days: 3, revenue: 300m, channel: SalesChannel.BookingCom, number: "LOC-2026-000001");
        var next = Rental(vehicle, First.AddDays(2), days: 2, revenue: 160m, channel: SalesChannel.Web, hour: 14, number: "LOC-2026-000002");

        var result = Compute(First, First.AddDays(4), Fleet(vehicle, First, 5), [late, next]);

        var shared = result.Rows.Single(r => r.Day == First.AddDays(2));
        shared.IsRented.ShouldBeTrue();
        shared.RentalRevenue.ShouldBe(Money.Tnd(180m));
        shared.Rentals.Count.ShouldBe(2);
        shared.Rentals.Select(r => (r.BookingId, r.Channel, r.Revenue)).ShouldBe(
            [(late.BookingId, SalesChannel.BookingCom, Money.Tnd(100m)), (next.BookingId, SalesChannel.Web, Money.Tnd(80m))],
            ignoreOrder: true);
        shared.BookingId.ShouldBe(late.BookingId);
        shared.BookingNumber.ShouldBe("LOC-2026-000001");
        shared.Channel.ShouldBe(SalesChannel.BookingCom);
        result.Rows.Count(r => r.IsRented).ShouldBe(4);
        result.Rows.Select(r => r.RentalRevenue).Sum().ShouldBe(Money.Tnd(460m));
    }

    [Fact]
    public void Revenue_without_a_vehicle_in_the_fleet_is_reported_apart_and_never_lost()
    {
        var vehicle = Guid.CreateVersion7();
        var unassigned = Rental(vehicle, First, days: 2, revenue: 120m) with { Vehicles = [] };
        var outOfFleet = Rental(Guid.CreateVersion7(), First, days: 1, revenue: 30m);

        var result = Compute(First, First.AddDays(2), Fleet(vehicle, First, 3), [unassigned, outOfFleet]);

        result.Rows.ShouldAllBe(r => !r.IsRented && r.RentalRevenue == Money.Zero);
        result.UnattributedRevenue.ShouldBe(Money.Tnd(150m));
        result.UnattributedDays.ShouldBe(3);
        result.Days.Select(d => (d.Day, d.UnattributedRevenue, d.UnattributedRentedDays)).ShouldBe(
        [
            (First, Money.Tnd(90m), 2),
            (First.AddDays(1), Money.Tnd(60m), 1),
            (First.AddDays(2), Money.Zero, 0),
        ]);
    }

    [Fact]
    public void Every_day_of_the_range_is_recorded_even_without_any_vehicle()
    {
        var vehicle = Guid.CreateVersion7();

        var result = Compute(First, First.AddDays(3), Fleet(vehicle, First.AddDays(1), 2), []);

        result.Days.Select(d => (d.Day, d.VehicleCount)).ShouldBe(
        [
            (First, 0),
            (First.AddDays(1), 1),
            (First.AddDays(2), 1),
            (First.AddDays(3), 0),
        ]);
        result.Days.ShouldAllBe(d => d.AgencyId == Agency && d.ComputedAtUtc == ComputedAt);
        result.Rows.ShouldAllBe(r => r.VehicleName == "Peugeot 208");
    }

    [Fact]
    public void A_rented_day_always_counts_as_available_and_a_blocked_day_without_rental_does_not()
    {
        var vehicle = Guid.CreateVersion7();
        var fleet = new[]
        {
            Day(vehicle, First, rentable: false),
            Day(vehicle, First.AddDays(1), rentable: false),
            Day(vehicle, First.AddDays(2), rentable: true),
        };
        var rental = Rental(vehicle, First, days: 1, revenue: 90m);

        var result = Compute(First, First.AddDays(2), fleet, [rental]);

        result.Rows.Select(r => (r.Day, r.IsAvailable, r.IsRented)).ShouldBe(
        [
            (First, true, true),
            (First.AddDays(1), false, false),
            (First.AddDays(2), true, false),
        ]);
    }

    [Fact]
    public void Costs_covering_a_period_are_spread_over_it_and_point_costs_stay_on_their_day()
    {
        var vehicle = Guid.CreateVersion7();
        var costs = new[]
        {
            new VehicleCostItem(vehicle, Money.Tnd(300m), First, First.AddDays(29)),
            new VehicleCostItem(vehicle, Money.Tnd(45m), First.AddDays(4), First.AddDays(4)),
            new VehicleCostItem(vehicle, Money.Tnd(70m), First.AddDays(40), First.AddDays(40)),
        };

        var result = SnapshotCalculator.Compute(Agency, First, First.AddDays(9), Fleet(vehicle, First, 10), [], costs, ComputedAt);

        result.Rows.Single(r => r.Day == First).DirectCosts.ShouldBe(Money.Tnd(10m));
        result.Rows.Single(r => r.Day == First.AddDays(4)).DirectCosts.ShouldBe(Money.Tnd(55m));
        result.Rows.Select(r => r.DirectCosts).Sum().ShouldBe(Money.Tnd(145m));
    }

    [Fact]
    public void A_cost_on_a_day_without_the_vehicle_in_the_fleet_is_not_counted()
    {
        var vehicle = Guid.CreateVersion7();
        var costs = new[] { new VehicleCostItem(vehicle, Money.Tnd(20m), First, First.AddDays(1)) };

        var result = SnapshotCalculator.Compute(Agency, First, First.AddDays(1), [Day(vehicle, First.AddDays(1))], [], costs, ComputedAt);

        result.Rows.ShouldHaveSingleItem().DirectCosts.ShouldBe(Money.Tnd(10m));
    }

    [Fact]
    public void The_margin_takes_the_commission_the_direct_costs_and_the_depreciation_off_the_revenue()
    {
        var vehicle = Guid.CreateVersion7();
        var rental = Rental(vehicle, First, days: 1, revenue: 120m, commission: 18m);
        var costs = new[] { new VehicleCostItem(vehicle, Money.Tnd(7.5m), First, First) };

        var row = SnapshotCalculator.Compute(Agency, First, First, [Day(vehicle, First, depreciation: 25.123m)], [rental], costs, ComputedAt)
            .Rows.ShouldHaveSingleItem();

        row.RentalRevenue.ShouldBe(Money.Tnd(120m));
        row.Commission.ShouldBe(Money.Tnd(18m));
        row.DirectCosts.ShouldBe(Money.Tnd(7.5m));
        row.Depreciation.ShouldBe(Money.Tnd(25.123m));
        row.Margin.ShouldBe(Money.Tnd(69.377m));
        row.AgencyId.ShouldBe(Agency);
        row.CategoryId.ShouldBe(Category);
        row.CategoryCode.ShouldBe("ECMR");
        row.ComputedAtUtc.ShouldBe(ComputedAt);
    }

    [Fact]
    public void Only_the_days_of_the_range_get_rows_and_shares()
    {
        var vehicle = Guid.CreateVersion7();
        var rental = Rental(vehicle, First, days: 10, revenue: 1000m);

        var result = Compute(First.AddDays(3), First.AddDays(4), Fleet(vehicle, First, 10), [rental]);

        result.Rows.Select(r => r.Day).ShouldBe([First.AddDays(3), First.AddDays(4)]);
        result.Rows.Select(r => r.RentalRevenue).Sum().ShouldBe(Money.Tnd(200m));
    }

    [Fact]
    public void Inconsistent_inputs_are_bugs()
    {
        var vehicle = Guid.CreateVersion7();

        Should.Throw<ArgumentException>(() => Compute(First.AddDays(1), First, [], []));
        Should.Throw<ArgumentException>(() => Compute(First, First, [Day(vehicle, First), Day(vehicle, First)], []));
        Should.Throw<ArgumentException>(() => Compute(First, First, [Day(vehicle, First) with { CategoryCode = "ECM" }], []));
        Should.Throw<ArgumentException>(() => Compute(First, First, [Day(vehicle, First) with { Depreciation = Money.Tnd(-1m) }], []));
    }

    [Fact]
    public void The_carrier_is_the_vehicle_of_the_last_span_started_at_or_before_the_instant()
    {
        var first = Guid.CreateVersion7();
        var second = Guid.CreateVersion7();
        var swap = new DateTimeOffset(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);
        CarrierSpan[] spans = [new(second, swap), new(first, null)];

        SnapshotCalculator.CarrierAt(spans, swap.AddMinutes(-1)).ShouldBe(first);
        SnapshotCalculator.CarrierAt(spans, swap).ShouldBe(second);
        SnapshotCalculator.CarrierAt([new(second, swap)], swap.AddDays(-1)).ShouldBe(second);
        SnapshotCalculator.CarrierAt([], swap).ShouldBeNull();
    }

    private static SnapshotComputation Compute(DateOnly from, DateOnly to, IEnumerable<FleetVehicleDay> fleet, IEnumerable<RevenueRental> rentals) =>
        SnapshotCalculator.Compute(Agency, from, to, fleet, rentals, [], ComputedAt);

    private static FleetVehicleDay Day(Guid vehicle, DateOnly day, bool rentable = true, decimal depreciation = 10m, string plate = "123 TU 4567") =>
        new(vehicle, plate, "Peugeot 208", day, Category, "ECMR", rentable, Money.Tnd(depreciation));

    private static List<FleetVehicleDay> Fleet(Guid vehicle, DateOnly from, int days, string plate = "123 TU 4567") =>
        [.. Enumerable.Range(0, days).Select(i => Day(vehicle, from.AddDays(i), plate: plate))];

    private static DateTimeOffset At(DateOnly day, int hour) => TunisTime.FromLocal(day, new TimeOnly(hour, 0));

    private static RevenueRental Rental(
        Guid vehicle,
        DateOnly pickupDay,
        int days,
        decimal revenue,
        decimal commission = 0m,
        SalesChannel channel = SalesChannel.Phone,
        int hour = 10,
        string number = "LOC-2026-000100") =>
        new(Guid.CreateVersion7(), number, channel, At(pickupDay, hour), days, Money.Tnd(revenue), Money.Tnd(commission), [new CarrierSpan(vehicle, null)]);

    private static VehicleDayFigures Figures(VehicleDaySnapshot row) =>
        new(row.VehicleId, row.Day, row.IsAvailable, row.IsRented, row.RentalRevenue, row.Commission, row.DirectCosts, row.Depreciation);
}
