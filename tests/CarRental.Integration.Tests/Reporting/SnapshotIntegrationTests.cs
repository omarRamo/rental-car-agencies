using CarRental.Application.Common.BackgroundJobs;
using CarRental.Application.Payments.Contracts;
using CarRental.Application.Reporting.Indicators;
using CarRental.Application.Reporting.Snapshots;
using CarRental.Domain.Common;
using CarRental.Domain.Identity;
using CarRental.Domain.Reporting;
using CarRental.Infrastructure.Persistence;
using CarRental.Integration.Tests.Bookings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static CarRental.Integration.Tests.Reporting.ReportingWorld;

namespace CarRental.Integration.Tests.Reporting;

/// <summary>The daily snapshots on SQL Server (ADR-0045): nightly job, recomputation, and the indicators read from them.</summary>
public sealed class SnapshotIntegrationTests(IntegrationHost host)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_nightly_job_gives_a_vehicle_rented_five_of_ten_available_days_a_utilization_of_one_half()
    {
        var agency = await CreateAsync(host, vehicles: 1, Ct);
        await BookingWorld.SetAsync(host, agency.AgencyId, "reporting.snapshot_refresh_days", "10", Ct);
        var file = await PastFileAsync(host, agency, agency.Vehicles[0], At(-8), days: 5, rentalInclVat: 595m, PastOutcome.Closed, Ct);

        await using (var scope = host.CreateScope())
        {
            var descriptor = scope.ServiceProvider.GetServices<RecurringJobDescriptor>().Single(d => d.JobId == "reporting.daily_snapshot");
            await ((IRecurringJob)scope.ServiceProvider.GetRequiredService(descriptor.JobType)).ExecuteAsync(Ct);
        }

        var rows = await RowsAsync(agency.AgencyId);
        rows.Select(r => r.Day).ShouldBe(Enumerable.Range(-10, 10).Select(Day));
        rows.ShouldAllBe(r => r.IsAvailable && r.VehicleId == agency.Vehicles[0] && r.CategoryCode == "ECMR");
        rows.Where(r => r.IsRented).Select(r => r.Day).ShouldBe(Enumerable.Range(-8, 5).Select(Day));
        rows.Where(r => r.IsRented).ShouldAllBe(r => r.BookingId == file.Id && r.BookingNumber == file.Number && r.Channel == SalesChannel.Phone);
        rows.Select(r => r.RentalRevenue).Sum().ShouldBe(Money.Tnd(500m));

        var report = await DashboardAsync(agency.AgencyId, Day(-10), Day(-1));
        report.Fleet.AvailableDays.ShouldBe(10);
        report.Fleet.RentedDays.ShouldBe(5);
        report.Fleet.Utilization.ShouldBe(0.5m);
        report.Fleet.RevenuePerAvailableDay.ShouldBe(Money.Tnd(50m));
        report.Fleet.AverageRentedDayPrice.ShouldBe(Money.Tnd(100m));
        report.Coverage.ShouldSatisfyAllConditions(c => c.ComputedDays.ShouldBe(10), c => c.IsComplete.ShouldBeTrue());
    }

    [Fact]
    public async Task Computing_a_period_again_replaces_its_days_with_the_same_figures_and_is_audited()
    {
        var agency = await CreateAsync(host, vehicles: 2, Ct);
        await PastFileAsync(host, agency, agency.Vehicles[0], At(-9), days: 3, rentalInclVat: 357m, PastOutcome.Returned, Ct);
        await PastFileAsync(host, agency, agency.Vehicles[1], At(-6), days: 4, rentalInclVat: 476m, PastOutcome.Closed, Ct, SalesChannel.BookingCom);

        var first = await RecomputeAsync(agency.AgencyId, Day(-12), Day(-1));
        var before = await RowsAsync(agency.AgencyId);
        var second = await RecomputeAsync(agency.AgencyId, Day(-12), Day(-1));
        var after = await RowsAsync(agency.AgencyId);

        first.Rows.ShouldBe(24);
        second.Rows.ShouldBe(24);
        after.Count.ShouldBe(24);
        after.Select(Figures).ShouldBe(before.Select(Figures));
        after.Select(r => r.Id).Intersect(before.Select(r => r.Id)).ShouldBeEmpty();
        after.Select(r => r.RentalRevenue).Sum().ShouldBe(Money.Tnd(700m));
        await using var scope = host.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CarRentalDbContext>();
        (await db.Set<VehicleDayRental>().CountAsync(r => r.AgencyId == agency.AgencyId, Ct)).ShouldBe(7);
        (await db.Set<SnapshotDay>().CountAsync(d => d.AgencyId == agency.AgencyId, Ct)).ShouldBe(12);
        (await db.Set<AuditLog>().CountAsync(a => a.Action == ReportingAuditActions.SnapshotsRecomputed && a.EntityId == agency.AgencyId.ToString(), Ct)).ShouldBe(2);
    }

    [Fact]
    public async Task Revenue_per_available_vehicle_day_matches_the_bookings_and_what_does_not_count_is_reported_apart()
    {
        var agency = await CreateAsync(host, vehicles: 2, Ct);
        var closed = await PastFileAsync(host, agency, agency.Vehicles[0], At(-10), days: 4, rentalInclVat: 476m, PastOutcome.Closed, Ct);
        var returned = await PastFileAsync(host, agency, agency.Vehicles[1], At(-6), days: 3, rentalInclVat: 357m, PastOutcome.Returned, Ct, SalesChannel.BookingCom);
        await PastFileAsync(host, agency, agency.Vehicles[0], At(-5), days: 2, rentalInclVat: 238m, PastOutcome.Cancelled, Ct, cancellationFee: Money.Tnd(50m));
        await PastFileAsync(host, agency, agency.Vehicles[1], At(-2), days: 1, rentalInclVat: 119m, PastOutcome.NoShow, Ct);
        await PastFileAsync(host, agency, null, At(-3), days: 2, rentalInclVat: 238m, PastOutcome.Confirmed, Ct, SalesChannel.Web);
        await RecomputeAsync(agency.AgencyId, Day(-12), Day(-1));

        var report = await DashboardAsync(agency.AgencyId, Day(-12), Day(-1));

        report.Fleet.Revenue.ShouldBe(Money.Tnd(700m), "400.000 + 300.000 HT; the cancelled file, the no-show and the file without vehicle bring nothing");
        report.Fleet.AvailableDays.ShouldBe(24);
        report.Fleet.RentedDays.ShouldBe(7);
        report.Fleet.RevenuePerAvailableDay.ShouldBe(Money.Tnd(700m / 24m));
        report.Fleet.Utilization.ShouldBe(7m / 24m);
        report.Fleet.AverageRentedDayPrice.ShouldBe(Money.Tnd(100m));
        report.Daily.Count.ShouldBe(12);
        report.Daily.Select(d => d.Totals.Revenue).Sum().ShouldBe(Money.Tnd(700m));
        report.Vehicles.Select(v => (v.VehicleId, v.Totals.Revenue, v.Totals.RentedDays)).ShouldBe(
            [(agency.Vehicles[0], Money.Tnd(400m), 4), (agency.Vehicles[1], Money.Tnd(300m), 3)],
            ignoreOrder: true);
        report.Channels.Channels.Single(c => c.Channel == SalesChannel.Phone).Revenue.ShouldBe(Money.Tnd(400m));
        report.Channels.Channels.Single(c => c.Channel == SalesChannel.BookingCom).ShouldSatisfyAllConditions(
            c => c.Revenue.ShouldBe(Money.Tnd(300m)),
            c => c.Bookings.ShouldBe(1),
            c => c.RentedDays.ShouldBe(3));
        report.Channels.UnattributedRevenue.ShouldBe(Money.Tnd(200m));
        report.Channels.Margin.ShouldBe(report.Fleet.Margin);

        var cancellations = report.Cancellations.Total;
        (cancellations.FirmBookings, cancellations.Cancelled, cancellations.NoShows).ShouldBe((5, 1, 1));
        cancellations.Fees.ShouldBe(Money.Tnd(50m));

        // Due on the returned and closed files: the quote TTC, contribution included (2.380 TTC per day).
        report.Cash.Unpaid.Select(u => (u.BookingId, u.Due, u.NetPaid)).ShouldBe(
            [(closed.Id, Money.Tnd(485.52m), Money.Zero), (returned.Id, Money.Tnd(364.14m), Money.Zero)],
            ignoreOrder: true);
        await using (var scope = host.CreateScope(Agent))
        {
            var paid = await scope.ServiceProvider.GetRequiredService<ICounterPayments>().RecordPaymentAsync(
                new CounterPaymentRequest(closed.Id, agency.AgencyId, PaymentMethodInfo.CardTerminal, 485.52m),
                Ct);
            paid.IsSuccess.ShouldBeTrue(paid.IsFailure ? paid.Error.ToString() : null);
        }

        var afterPayment = await DashboardAsync(agency.AgencyId, Day(-12), Day(-1));
        afterPayment.Cash.Unpaid.ShouldHaveSingleItem().BookingId.ShouldBe(returned.Id);
        afterPayment.Cash.UnpaidTotal.ShouldBe(Money.Tnd(364.14m));
        afterPayment.Sources.InvoicingConnected.ShouldBeFalse();
        afterPayment.Sources.CostSources.ShouldBe(["fleet"]);
    }

    [Fact]
    public async Task A_vehicle_swapped_during_the_rental_hands_its_following_days_to_the_replacement()
    {
        var agency = await CreateAsync(host, vehicles: 2, Ct);
        await PastFileAsync(host, agency, agency.Vehicles[0], At(-8), days: 4, rentalInclVat: 476m, PastOutcome.Closed, Ct, swapTo: agency.Vehicles[1]);
        await RecomputeAsync(agency.AgencyId, Day(-8), Day(-5));

        var rows = await RowsAsync(agency.AgencyId);

        // Units start at 10:00; the swap happens at 14:00 on the second day: that day stays with the first vehicle.
        rows.Where(r => r.IsRented && r.VehicleId == agency.Vehicles[0]).Select(r => r.Day).ShouldBe([Day(-8), Day(-7)]);
        rows.Where(r => r.IsRented && r.VehicleId == agency.Vehicles[1]).Select(r => r.Day).ShouldBe([Day(-6), Day(-5)]);
    }

    [Fact]
    public async Task The_dashboard_tile_shows_the_month_so_far_against_the_same_days_of_the_previous_month()
    {
        var agency = await CreateAsync(host, vehicles: 1, Ct);
        var period = ReportPeriod.DefaultFor(Day(0));
        var covered = period.Until(Day(-1))!;
        var previous = period.Previous(covered.Days);
        await PastFileAsync(host, agency, agency.Vehicles[0], TunisTime.FromLocal(covered.To, new TimeOnly(10, 0)), days: 1, rentalInclVat: 119m, PastOutcome.Closed, Ct);
        await PastFileAsync(host, agency, agency.Vehicles[0], TunisTime.FromLocal(previous.From, new TimeOnly(10, 0)), days: 2, rentalInclVat: 476m, PastOutcome.Closed, Ct);
        await RecomputeAsync(agency.AgencyId, previous.From, covered.To);

        await using var scope = host.CreateScope(Accountant);
        var queries = scope.ServiceProvider.GetRequiredService<ReportQueries>();
        var headline = await queries.GetHeadlineAsync(agency.AgencyId, Ct);

        headline.IsSuccess.ShouldBeTrue(headline.IsFailure ? headline.Error.ToString() : null);
        var tile = headline.Value;
        (tile.Period.From, tile.Period.To, tile.Period.CoveredTo).ShouldBe((period.From, period.To, (DateOnly?)covered.To));
        (tile.Period.PreviousFrom, tile.Period.PreviousTo).ShouldBe(((DateOnly?)previous.From, (DateOnly?)previous.To));
        tile.Coverage.IsComplete.ShouldBeTrue();
        (tile.Fleet.AvailableDays, tile.Fleet.RentedDays, tile.Fleet.Revenue).ShouldBe((covered.Days, 1, Money.Tnd(100m)));
        tile.Fleet.RevenuePerAvailableDay.ShouldBe(Money.Tnd(100m / covered.Days));
        var previousRented = Math.Min(2, previous.Days);
        (tile.PreviousFleet.RentedDays, tile.PreviousFleet.Revenue).ShouldBe((previousRented, Money.Tnd(200m * previousRented)));

        // The same figures as the reports screen over the same days.
        var report = await queries.GetDashboardAsync(new ReportRequest(agency.AgencyId, period.From, period.To), Ct);
        report.Value.Fleet.ShouldBe(tile.Fleet);
        await using var agent = host.CreateScope(Agent);
        (await agent.ServiceProvider.GetRequiredService<ReportQueries>().GetHeadlineAsync(agency.AgencyId, Ct)).Error.ShouldBe(ReportingErrors.Forbidden);
        (await queries.GetHeadlineAsync(Guid.CreateVersion7(), Ct)).Error.ShouldBe(ReportingErrors.AgencyNotFound);
    }

    [Fact]
    public async Task Only_past_days_are_computed_and_only_with_access_to_the_reports()
    {
        var agency = await CreateAsync(host, vehicles: 1, Ct);

        await using var scope = host.CreateScope(Agent);
        var service = scope.ServiceProvider.GetRequiredService<SnapshotService>();
        (await service.RecomputeAsync(agency.AgencyId, Day(-3), Day(-1), Ct)).Error.ShouldBe(ReportingErrors.Forbidden);
        await using var manager = host.CreateScope(Manager);
        (await manager.ServiceProvider.GetRequiredService<SnapshotService>().RecomputeAsync(agency.AgencyId, Day(-3), Day(0), Ct))
            .Error.ShouldBe(ReportingErrors.SnapshotFutureDay);
        (await scope.ServiceProvider.GetRequiredService<ReportQueries>().GetDashboardAsync(new ReportRequest(agency.AgencyId, Day(-3), Day(-1)), Ct))
            .Error.ShouldBe(ReportingErrors.Forbidden);
    }

    private static (Guid, DateOnly, bool, bool, Guid?, Money, Money, Money, Money) Figures(VehicleDaySnapshot row) =>
        (row.VehicleId, row.Day, row.IsAvailable, row.IsRented, row.BookingId, row.RentalRevenue, row.Commission, row.DirectCosts, row.Depreciation);

    private async Task<SnapshotRun> RecomputeAsync(Guid agencyId, DateOnly from, DateOnly to)
    {
        await using var scope = host.CreateScope(Manager);
        var run = await scope.ServiceProvider.GetRequiredService<SnapshotService>().RecomputeAsync(agencyId, from, to, Ct);
        run.IsSuccess.ShouldBeTrue(run.IsFailure ? run.Error.ToString() : null);
        return run.Value;
    }

    private async Task<ReportDashboard> DashboardAsync(Guid agencyId, DateOnly from, DateOnly to)
    {
        await using var scope = host.CreateScope(Accountant);
        var report = await scope.ServiceProvider.GetRequiredService<ReportQueries>().GetDashboardAsync(new ReportRequest(agencyId, from, to), Ct);
        report.IsSuccess.ShouldBeTrue(report.IsFailure ? report.Error.ToString() : null);
        return report.Value;
    }

    private async Task<List<VehicleDaySnapshot>> RowsAsync(Guid agencyId)
    {
        await using var scope = host.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<CarRentalDbContext>().Set<VehicleDaySnapshot>()
            .AsNoTracking()
            .Where(s => s.AgencyId == agencyId)
            .OrderBy(s => s.VehicleId)
            .ThenBy(s => s.Day)
            .ToListAsync(Ct);
    }
}
