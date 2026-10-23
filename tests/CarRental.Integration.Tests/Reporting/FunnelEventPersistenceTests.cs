using CarRental.Application.Common.BackgroundJobs;
using CarRental.Application.Common.Persistence;
using CarRental.Application.Reporting.Contracts;
using CarRental.Domain.Common;
using CarRental.Domain.Identity;
using CarRental.Domain.Reporting;
using CarRental.Integration.Tests.Fleet;
using CarRental.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CarRental.Integration.Tests.Reporting;

/// <summary>Funnel events on SQL Server: recorded by the public recorder, purged after the retention period.</summary>
public sealed class FunnelEventPersistenceTests(IntegrationHost host)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_recorder_writes_an_anonymous_event_for_the_agency_of_the_public_site()
    {
        var visitId = Guid.NewGuid();
        var bookingId = Guid.CreateVersion7();
        var agencyId = await FleetData.NewAgencyAsync(host, Ct);
        await using (var visit = host.CreateScope(TestActors.Visitor(agencyId)))
        {
            await visit.ServiceProvider.GetRequiredService<IFunnelEvents>().RecordAsync(
                new FunnelEventRecord(FunnelStepInfo.Results, visitId, "en", SalesChannel.Web, Source: "google", UtmMedium: "cpc", CategoryCode: "EDAR", BookingId: bookingId),
                Ct);
        }

        await using var scope = host.CreateScope();
        var saved = await scope.ServiceProvider.GetRequiredService<IAppDbContext>().Set<FunnelEvent>()
            .AsNoTracking()
            .SingleAsync(e => e.VisitId == visitId, Ct);
        saved.AgencyId.ShouldBe(agencyId);
        saved.Step.ShouldBe(FunnelStep.Results);
        saved.Culture.ShouldBe("en");
        saved.Source.ShouldBe("google");
        saved.UtmMedium.ShouldBe("cpc");
        saved.CategoryCode!.Value.ShouldBe("EDAR");
        saved.BookingId.ShouldBe(bookingId);
        saved.OccurredAtUtc.ShouldBeInRange(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddMinutes(1));
    }

    [Fact]
    public async Task Events_older_than_the_retention_period_are_purged_and_the_purge_is_audited()
    {
        var visitId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var (first, second) = (await FleetData.NewAgencyAsync(host, Ct), await FleetData.NewAgencyAsync(host, Ct));
        await using (var scope = host.CreateScope())
        {
            // The purge runs for the whole platform: the old steps of every agency go.
            var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
            foreach (var (age, agencyId) in new[] { (401, first), (900, second), (10, first) })
            {
                db.Set<FunnelEvent>().Add(FunnelEvent.Record(agencyId, new FunnelEventDetails(visitId, FunnelStep.Search, "fr", SalesChannel.Web), now.AddDays(-age)).Value);
            }

            await db.SaveChangesAsync(Ct);
        }

        await using (var scope = host.CreateScope())
        {
            var descriptor = scope.ServiceProvider.GetServices<RecurringJobDescriptor>().Single(d => d.JobId == "reporting.purge_funnel_events");
            await ((IRecurringJob)scope.ServiceProvider.GetRequiredService(descriptor.JobType)).ExecuteAsync(Ct);
        }

        await using var check = host.CreateScope();
        var checkDb = check.ServiceProvider.GetRequiredService<IAppDbContext>();
        var left = await checkDb.Set<FunnelEvent>().AsNoTracking().Where(e => e.VisitId == visitId).ToListAsync(Ct);
        left.ShouldHaveSingleItem().OccurredAtUtc.ShouldBeGreaterThan(now.AddDays(-11));
        var audit = await checkDb.Set<AuditLog>().AsNoTracking()
            .Where(a => a.Action == ReportingAuditActions.FunnelEventsPurged && a.OccurredAtUtc >= now.AddMinutes(-1))
            .ToListAsync(Ct);
        audit.ShouldNotBeEmpty();
    }
}
