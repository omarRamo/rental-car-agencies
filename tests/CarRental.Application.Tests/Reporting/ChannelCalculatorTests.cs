using CarRental.Application.Reporting.Indicators;
using CarRental.Domain.Common;
using CarRental.Domain.Reporting;

namespace CarRental.Application.Tests.Reporting;

/// <summary>Revenue and margin by channel after commissions (ADR-0046).</summary>
public sealed class ChannelCalculatorTests
{
    private static readonly DateOnly First = new(2026, 9, 1);

    [Fact]
    public void Channels_get_their_shares_and_the_costs_of_the_days_they_rented()
    {
        var vehicle = Guid.CreateVersion7();
        var other = Guid.CreateVersion7();
        var web = Guid.CreateVersion7();
        var bookingCom = Guid.CreateVersion7();
        SnapshotRowFigures[] rows =
        [
            Row(vehicle, First, rented: true, SalesChannel.Web, revenue: 100m, commission: 0m, costs: 10m, depreciation: 20m),
            Row(vehicle, First.AddDays(1), rented: true, SalesChannel.BookingCom, revenue: 150m, commission: 22.5m, costs: 5m, depreciation: 20m),
            Row(other, First, rented: false, null, revenue: 0m, commission: 0m, costs: 0m, depreciation: 20m),
        ];
        RentalShareFigures[] shares =
        [
            new(web, SalesChannel.Web, First, Money.Tnd(100m), Money.Zero),
            new(bookingCom, SalesChannel.BookingCom, First.AddDays(1), Money.Tnd(150m), Money.Tnd(22.5m)),
        ];

        var report = ChannelCalculator.Compute(rows, shares, Money.Tnd(12m));

        report.Channels.Select(c => c.Channel).ShouldBe(Enum.GetValues<SalesChannel>());
        var ofWeb = report.Channels.Single(c => c.Channel == SalesChannel.Web);
        (ofWeb.Bookings, ofWeb.RentedDays, ofWeb.Revenue, ofWeb.Commission, ofWeb.AllocatedCosts, ofWeb.Margin)
            .ShouldBe((1, 1, Money.Tnd(100m), Money.Zero, Money.Tnd(30m), Money.Tnd(70m)));
        var ofBookingCom = report.Channels.Single(c => c.Channel == SalesChannel.BookingCom);
        (ofBookingCom.NetRevenue, ofBookingCom.AllocatedCosts, ofBookingCom.Margin).ShouldBe((Money.Tnd(127.5m), Money.Tnd(25m), Money.Tnd(102.5m)));
        report.IdleCosts.ShouldBe(Money.Tnd(20m));
        report.UnattributedRevenue.ShouldBe(Money.Tnd(12m));
        report.ShareOf(ofBookingCom).ShouldBe(0.6m);
        report.Channels.Single(c => c.Channel == SalesChannel.Phone).ShouldSatisfyAllConditions(
            phone => phone.Revenue.ShouldBe(Money.Zero),
            phone => phone.AverageRentedDayPrice.ShouldBeNull());

        // The channel margins without the idle costs make the margin of the fleet.
        report.Margin.ShouldBe(FleetDayTotals.Of(rows.Select(r => r.Figures)).Margin);
    }

    [Fact]
    public void A_day_shared_by_two_rentals_splits_the_revenue_exactly_and_its_costs_follow_the_main_rental()
    {
        var vehicle = Guid.CreateVersion7();
        var late = Guid.CreateVersion7();
        var next = Guid.CreateVersion7();
        SnapshotRowFigures[] rows = [Row(vehicle, First, rented: true, SalesChannel.BookingCom, revenue: 180m, commission: 15m, costs: 0m, depreciation: 20m)];
        RentalShareFigures[] shares =
        [
            new(late, SalesChannel.BookingCom, First, Money.Tnd(100m), Money.Tnd(15m)),
            new(next, SalesChannel.Web, First, Money.Tnd(80m), Money.Zero),
        ];

        var report = ChannelCalculator.Compute(rows, shares, Money.Zero);

        report.Revenue.ShouldBe(Money.Tnd(180m));
        report.Channels.Single(c => c.Channel == SalesChannel.Web).ShouldSatisfyAllConditions(
            c => c.Revenue.ShouldBe(Money.Tnd(80m)),
            c => c.RentedDays.ShouldBe(1),
            c => c.AllocatedCosts.ShouldBe(Money.Zero));
        report.Channels.Single(c => c.Channel == SalesChannel.BookingCom).AllocatedCosts.ShouldBe(Money.Tnd(20m));
    }

    [Fact]
    public void Without_any_revenue_shares_have_no_value()
    {
        var report = ChannelCalculator.Compute([], [], Money.Zero);

        report.ShareOf(report.Channels[0]).ShouldBeNull();
        report.Margin.ShouldBe(Money.Zero);
    }

    private static SnapshotRowFigures Row(Guid vehicle, DateOnly day, bool rented, SalesChannel? channel, decimal revenue, decimal commission, decimal costs, decimal depreciation) =>
        new(vehicle, "123 TU 4567", "Peugeot 208", "ECMR", day, true, rented, channel, Money.Tnd(revenue), Money.Tnd(commission), Money.Tnd(costs), Money.Tnd(depreciation));
}
