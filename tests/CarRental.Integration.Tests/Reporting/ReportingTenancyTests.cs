using CarRental.Application.Common.Persistence;
using CarRental.Application.Identity.Contracts;
using CarRental.Application.Reporting.Indicators;
using CarRental.Application.Reporting.Snapshots;
using CarRental.Domain.Common;
using CarRental.Domain.Reporting;
using CarRental.Infrastructure.Persistence.Interceptors;
using CarRental.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static CarRental.Integration.Tests.Reporting.ReportingWorld;

namespace CarRental.Integration.Tests.Reporting;

/// <summary>
/// The reports of two agencies never meet (ADR-0067), on SQL Server: the staff of an agency read and recompute the
/// snapshots and indicators of their own agency only, and the funnel of a public site counts for its agency.
/// </summary>
public sealed class ReportingTenancyTests(IntegrationHost host)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_staff_of_an_agency_read_and_recompute_their_own_reports_only()
    {
        var (a, b) = (await CreateAsync(host, vehicles: 1, Ct), await CreateAsync(host, vehicles: 1, Ct));
        await PastFileAsync(host, a, a.Vehicles[0], At(-8), days: 5, rentalInclVat: 595m, PastOutcome.Closed, Ct);
        await PastFileAsync(host, b, b.Vehicles[0], At(-6), days: 2, rentalInclVat: 238m, PastOutcome.Returned, Ct);
        await RecomputeAsync(a.AgencyId);
        await RecomputeAsync(b.AgencyId);

        await using var scope = host.CreateScope(TestActors.Staff(a.AgencyId, Roles.Manager));
        var reports = scope.ServiceProvider.GetRequiredService<ReportQueries>();
        var own = (await reports.GetDashboardAsync(new ReportRequest(a.AgencyId, Day(-10), Day(-1)), Ct)).Value;
        own.Fleet.Revenue.ShouldBe(Money.Tnd(500m), "the rental of the agency, none of the other");
        own.Vehicles.Select(v => v.VehicleId).ShouldBe([a.Vehicles[0]]);
        (await reports.GetHeadlineAsync(a.AgencyId, Ct)).Value.AgencyId.ShouldBe(a.AgencyId);
        (await reports.GetDashboardAsync(new ReportRequest(b.AgencyId, Day(-10), Day(-1)), Ct)).Error.ShouldBe(ReportingErrors.AgencyNotFound);
        (await reports.GetHeadlineAsync(b.AgencyId, Ct)).Error.ShouldBe(ReportingErrors.AgencyNotFound);
        (await scope.ServiceProvider.GetRequiredService<SnapshotService>().RecomputeAsync(b.AgencyId, Day(-10), Day(-1), Ct))
            .Error.ShouldBe(ReportingErrors.AgencyNotFound);

        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        (await db.Set<VehicleDaySnapshot>().AnyAsync(s => s.AgencyId == b.AgencyId, Ct)).ShouldBeFalse();
        (await db.Set<VehicleDayRental>().AnyAsync(r => r.AgencyId == b.AgencyId, Ct)).ShouldBeFalse();
        (await db.Set<SnapshotDay>().AnyAsync(d => d.AgencyId == b.AgencyId, Ct)).ShouldBeFalse();
        (await db.Set<VehicleDaySnapshot>().Select(s => s.AgencyId).Distinct().ToListAsync(Ct)).ShouldBe([a.AgencyId]);

        // Even a write made without the services is refused by the persistence layer.
        db.Set<FunnelEvent>().Add(FunnelEvent.Record(b.AgencyId, new FunnelEventDetails(Guid.NewGuid(), FunnelStep.Search, "fr", SalesChannel.Web), DateTimeOffset.UtcNow).Value);
        await Should.ThrowAsync<AgencyBoundaryException>(() => db.SaveChangesAsync(Ct));

        await using var check = host.CreateScope();
        (await check.ServiceProvider.GetRequiredService<IAppDbContext>().Set<VehicleDaySnapshot>().CountAsync(s => s.AgencyId == b.AgencyId, Ct))
            .ShouldBeGreaterThan(0, "the snapshots of the other agency are still there");
    }

    [Fact]
    public async Task The_funnel_of_a_public_site_counts_for_its_agency_only()
    {
        var (a, b) = (await CreateAsync(host, vehicles: 1, Ct), await CreateAsync(host, vehicles: 1, Ct));
        var source = $"site{Guid.NewGuid():N}"[..20];
        await using (var scope = host.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
            foreach (var (agencyId, searches) in new[] { (a.AgencyId, 3), (b.AgencyId, 5) })
            {
                for (var i = 0; i < searches; i++)
                {
                    db.Set<FunnelEvent>().Add(FunnelEvent.Record(agencyId, new FunnelEventDetails(Guid.NewGuid(), FunnelStep.Search, "fr", SalesChannel.Web, source), At(-2)).Value);
                }
            }

            await db.SaveChangesAsync(Ct);
        }

        foreach (var (agency, searches) in new[] { (a, 3), (b, 5) })
        {
            await using var scope = host.CreateScope(TestActors.Staff(agency.AgencyId, Roles.Manager));
            var report = (await scope.ServiceProvider.GetRequiredService<ReportQueries>()
                .GetDashboardAsync(new ReportRequest(agency.AgencyId, Day(-10), Day(-1)), Ct)).Value;
            report.Funnel.Rows.Single(r => r.Source == source).Searches.ShouldBe(searches);
            report.Funnel.Total.Searches.ShouldBe(searches);
        }
    }

    private async Task RecomputeAsync(Guid agencyId)
    {
        await using var scope = host.CreateScope(Manager);
        var run = await scope.ServiceProvider.GetRequiredService<SnapshotService>().RecomputeAsync(agencyId, Day(-10), Day(-1), Ct);
        run.IsSuccess.ShouldBeTrue(run.IsFailure ? run.Error.ToString() : null);
    }
}
