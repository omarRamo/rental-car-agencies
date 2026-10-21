using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Fleet.Contracts;
using CarRental.Application.Reporting.Contracts;
using CarRental.Application.Reporting.Snapshots;
using CarRental.Domain.Common;
using CarRental.Domain.Reporting;
using static CarRental.Application.Tests.Reporting.ReportingTestData;

namespace CarRental.Application.Tests.Reporting;

/// <summary>What the snapshots are computed from: fleet days, counted files with their commission, every cost source (ADR-0045).</summary>
public sealed class SnapshotInputsLoaderTests
{
    private static readonly DateOnly First = new(2026, 9, 1);

    [Fact]
    public async Task Only_firm_files_become_revenue_with_their_commission_and_without_the_contribution()
    {
        var vehicle = Guid.CreateVersion7();
        var bookings = new FakeBookingsReporting();
        var confirmed = Booking(At(First, 10), 3, revenue: 300m, channel: SalesChannel.BookingCom, vehicle: vehicle);
        var cancelled = Booking(At(First, 10), 2, vehicle: vehicle, steps: [(BookingStatusInfo.Confirmed, At(First.AddDays(-9), 9)), (BookingStatusInfo.Cancelled, At(First.AddDays(-1), 9))]);
        var option = Booking(At(First.AddDays(1), 10), 2, vehicle: vehicle, steps: [(BookingStatusInfo.OptionHeld, At(First.AddDays(-2), 9))]);
        var noShow = Booking(At(First.AddDays(2), 10), 2, vehicle: vehicle, steps: [(BookingStatusInfo.Confirmed, At(First.AddDays(-2), 9)), (BookingStatusInfo.NoShow, At(First.AddDays(2), 13))]);
        bookings.Bookings.AddRange([confirmed, cancelled, option, noShow]);
        var commissions = new FakeCommissions();
        commissions.Commissions[confirmed.BookingId] = Money.Tnd(45m);
        var fleet = new FakeFleetReporting();
        fleet.Vehicles.Add(new FleetVehicleHistoryInfo(
            vehicle,
            "210 TU 5555",
            "Renault",
            "Clio",
            First.AddYears(-1),
            null,
            [.. Enumerable.Range(0, 5).Select(i => new FleetVehicleDayInfo(First.AddDays(i), Ecmr, "ECMR", true, Money.Tnd(12.5m)))]));
        var expenses = new FakeCostSource("expenses");
        expenses.Costs.Add(new VehicleCostInfo(vehicle, Money.Tnd(80m), First, First, "Lavage"));
        var loader = new SnapshotInputsLoader(fleet, bookings, [new FakeCostSource("fleet"), expenses], commissions);

        var inputs = await loader.LoadAsync(Agency, First, First.AddDays(4), TestContext.Current.CancellationToken);

        var rental = inputs.Rentals.ShouldHaveSingleItem();
        rental.BookingId.ShouldBe(confirmed.BookingId);
        rental.Revenue.ShouldBe(Money.Tnd(300m));
        rental.Commission.ShouldBe(Money.Tnd(45m));
        rental.Channel.ShouldBe(SalesChannel.BookingCom);
        rental.Days.ShouldBe(3);
        rental.Vehicles.ShouldBe([new CarrierSpan(vehicle, null)]);
        commissions.Asked.ShouldHaveSingleItem().ShouldBe([confirmed.BookingId]);
        inputs.Fleet.Count.ShouldBe(5);
        inputs.Fleet[0].ShouldBe(new FleetVehicleDay(vehicle, "210 TU 5555", "Renault Clio", First, Ecmr, "ECMR", true, Money.Tnd(12.5m)));
        inputs.Costs.ShouldBe([new VehicleCostItem(vehicle, Money.Tnd(80m), First, First)]);
    }

    [Fact]
    public async Task Without_counted_files_the_commission_source_is_not_asked()
    {
        var commissions = new FakeCommissions();
        var loader = new SnapshotInputsLoader(new FakeFleetReporting(), new FakeBookingsReporting(), [], commissions);

        var inputs = await loader.LoadAsync(Agency, First, First, TestContext.Current.CancellationToken);

        inputs.Rentals.ShouldBeEmpty();
        commissions.Asked.ShouldBeEmpty();
    }
}
