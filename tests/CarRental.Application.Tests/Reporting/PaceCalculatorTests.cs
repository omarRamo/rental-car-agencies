using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Fleet.Contracts;
using CarRental.Application.Reporting.Indicators;
using CarRental.Domain.Common;
using static CarRental.Application.Tests.Reporting.ReportingTestData;

namespace CarRental.Application.Tests.Reporting;

/// <summary>The booking pace at 30, 60 and 90 days, from the files and the fleet (ADR-0046).</summary>
public sealed class PaceCalculatorTests
{
    private static readonly DateOnly Today = new(2026, 10, 21);
    private static readonly DateTimeOffset Now = At(Today, 9);
    private static readonly PaceCategory[] Categories = [new(Ecmr, "ECMR"), new(Cdmr, "CDMR")];

    [Fact]
    public void Firm_and_tentative_days_are_counted_by_category_within_each_horizon()
    {
        BookingFactsInfo[] bookings =
        [
            Booking(At(Today.AddDays(2), 10), 5),
            Booking(At(Today.AddDays(-2), 10), 4, steps: [(BookingStatusInfo.InProgress, At(Today.AddDays(-2), 10))]),
            Booking(At(Today.AddDays(40), 10), 3, steps: [(BookingStatusInfo.OptionHeld, Now.AddDays(-1))]),
            Booking(At(Today.AddDays(85), 10), 10, category: Cdmr),
            Booking(At(Today.AddDays(5), 10), 3, steps: [(BookingStatusInfo.Confirmed, Now.AddDays(-9)), (BookingStatusInfo.Cancelled, Now.AddDays(-1))]),
        ];
        FleetVehicleHistoryInfo[] fleet = [Vehicle(Ecmr, 90), Vehicle(Cdmr, 60)];

        var pace = PaceCalculator.Compute(Today, PaceCalculator.DefaultHorizons, Categories, bookings, fleet, [], Today.AddYears(-1), Now.AddYears(-1));

        pace.AsOf.ShouldBe(Today);
        pace.Horizons.ShouldBe([30, 60, 90]);
        var ecmr = pace.Categories.Single(c => c.CategoryId == Ecmr).Horizons;
        ecmr.Select(h => (h.Days, h.SoldDays, h.TentativeDays, h.CapacityDays)).ShouldBe([(30, 7, 0, 30), (60, 7, 3, 60), (90, 7, 3, 90)]);
        var cdmr = pace.Categories.Single(c => c.CategoryId == Cdmr).Horizons;
        cdmr.Select(h => (h.SoldDays, h.CapacityDays)).ShouldBe([(0, 30), (0, 60), (5, 60)]);
        pace.Totals.Select(h => h.SoldDays).ShouldBe([7, 7, 12]);
        ecmr[0].Occupancy.ShouldBe(7m / 30m);
        ecmr[1].OccupancyWithTentative.ShouldBe(10m / 60m);
        ecmr.ShouldAllBe(h => h.LastYearSoldDays == null && h.ChangeOnLastYear == null);
    }

    [Fact]
    public void Last_year_counts_the_files_as_they_stood_at_the_same_lead_time()
    {
        var lastYearToday = Today.AddYears(-1);
        var lastYearNow = Now.AddYears(-1);
        BookingFactsInfo[] lastYear =
        [
            // Confirmed before the same instant last year: sold at that lead time.
            Booking(At(lastYearToday.AddDays(3), 10), 4, steps: [(BookingStatusInfo.Confirmed, lastYearNow.AddDays(-20)), (BookingStatusInfo.Closed, lastYearNow.AddDays(10))]),

            // Still an option at that instant, confirmed later: not sold yet then.
            Booking(At(lastYearToday.AddDays(10), 10), 2, steps: [(BookingStatusInfo.OptionHeld, lastYearNow.AddDays(-2)), (BookingStatusInfo.Confirmed, lastYearNow.AddDays(1))]),

            // Booked after that instant: not on the books yet.
            Booking(At(lastYearToday.AddDays(20), 10), 3, createdAt: lastYearNow.AddDays(5), steps: [(BookingStatusInfo.Confirmed, lastYearNow.AddDays(5))]),
        ];
        BookingFactsInfo[] now = [Booking(At(Today.AddDays(3), 10), 6)];

        var pace = PaceCalculator.Compute(Today, PaceCalculator.DefaultHorizons, Categories, now, [Vehicle(Ecmr, 90)], lastYear, lastYearToday, lastYearNow);

        var ecmr = pace.Categories.Single(c => c.CategoryId == Ecmr).Horizons;
        ecmr.Select(h => h.LastYearSoldDays).ShouldBe([4, 4, 4]);
        ecmr[0].ChangeOnLastYear.ShouldBe(0.5m);
    }

    private static FleetVehicleHistoryInfo Vehicle(Guid category, int rentableDays) =>
        new(
            Guid.CreateVersion7(),
            "200 TU 1000",
            "Renault",
            "Clio",
            Today.AddYears(-1),
            null,
            [.. Enumerable.Range(0, 90).Select(i => new FleetVehicleDayInfo(Today.AddDays(i), category, category == Ecmr ? "ECMR" : "CDMR", i < rentableDays, Money.Zero))]);
}
