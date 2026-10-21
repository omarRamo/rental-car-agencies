using CarRental.Application.Common.Persistence;
using CarRental.Application.Common.Seeding;
using CarRental.Application.Referential.Contracts;
using CarRental.Domain.Common;
using CarRental.Domain.Reporting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CarRental.Application.Reporting.Snapshots;

/// <summary>
/// Demo data of the reports (development and recette only), after the demo files and the finance data: the snapshots of
/// the demo agency over the last <see cref="SnapshotDays"/> days (computed from the demo fleet and files, like the nightly
/// job would have), and anonymous funnel events of the source <see cref="DemoSource"/> over the last
/// <see cref="FunnelDays"/> days, so that the conversion table has searches to compare the demo requests with.
/// Idempotent: the days already computed are kept, the funnel events are written once.
/// </summary>
internal sealed partial class ReportingDemoSeeder(
    IReferentialQueries referential,
    IAppDbContext db,
    SnapshotWriter writer,
    TimeProvider clock,
    ILogger<ReportingDemoSeeder> logger) : IDataSeeder
{
    /// <summary>Name of the demo agency created by the Referential demo seeder.</summary>
    public const string DemoAgencyName = "Agence Démo Tunis";

    /// <summary>Source of the demo files and funnel events.</summary>
    public const string DemoSource = "demo";

    public const int SnapshotDays = 45;

    public const int FunnelDays = 30;

    public SeedStage Stage => SeedStage.Demo;

    /// <summary>After the finance data (800): the reports read everything else.</summary>
    public int Order => 950;

    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        var agency = (await referential.GetAgenciesAsync(cancellationToken)).FirstOrDefault(a => a.Name == DemoAgencyName);
        if (agency is null)
        {
            LogNoAgency(DemoAgencyName);
            return;
        }

        var yesterday = TunisTime.Today(clock).AddDays(-1);
        var from = yesterday.AddDays(1 - SnapshotDays);
        var computed = await db.Set<SnapshotDay>()
            .AsNoTracking()
            .CountAsync(d => d.AgencyId == agency.Id && d.Day >= from && d.Day <= yesterday, cancellationToken);
        if (computed < SnapshotDays)
        {
            var run = await writer.RecomputeAsync(agency.Id, from, yesterday, cancellationToken);
            if (run.IsFailure)
            {
                LogSnapshotsSkipped(run.Error.Code);
            }
        }

        if (!await db.Set<FunnelEvent>().AsNoTracking().AnyAsync(e => e.Source == DemoSource, cancellationToken))
        {
            db.Set<FunnelEvent>().AddRange(DemoFunnel(yesterday));
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    /// <summary>
    /// A steady demo traffic: every day about ten to sixteen searches on the website and two to five through WhatsApp
    /// links, four searches out of five showing results; anonymous visits, French and English.
    /// </summary>
    private static IEnumerable<FunnelEvent> DemoFunnel(DateOnly lastDay)
    {
        for (var day = lastDay.AddDays(1 - FunnelDays); day <= lastDay; day = day.AddDays(1))
        {
            foreach (var (channel, searches) in new[] { (SalesChannel.Web, 10 + (day.DayNumber % 7)), (SalesChannel.WhatsApp, 2 + (day.DayNumber % 4)) })
            {
                for (var i = 0; i < searches; i++)
                {
                    var visit = Guid.NewGuid();
                    var at = TunisTime.FromLocal(day, new TimeOnly(8, 0)).AddMinutes((37 * i) % 720);
                    var details = new FunnelEventDetails(visit, FunnelStep.Search, i % 3 == 0 ? "en" : "fr", channel, DemoSource);
                    yield return FunnelEvent.Record(details, at).Value;
                    if (i % 5 != 4)
                    {
                        yield return FunnelEvent.Record(details with { Step = FunnelStep.Results }, at.AddSeconds(2)).Value;
                    }
                }
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Demo agency {Agency} not found: no demo reports.")]
    private partial void LogNoAgency(string agency);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Demo snapshots skipped: {Code}.")]
    private partial void LogSnapshotsSkipped(string code);
}
