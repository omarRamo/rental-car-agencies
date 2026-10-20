using CarRental.Application.Common.Persistence;
using CarRental.Application.Common.Security;
using CarRental.Application.Reporting;
using CarRental.Application.Reporting.Contracts;
using CarRental.Application.Reporting.Funnel;
using CarRental.Application.Tests.Fleet.Fakes;
using CarRental.Domain.Common;
using CarRental.Domain.Reporting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace CarRental.Application.Tests.Reporting;

/// <summary>The funnel recorder writes in its own scope and never lets a failure reach the page.</summary>
public sealed class FunnelEventRecorderTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 14, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static FunnelEventRecord SearchRecord(Guid? visitId = null) => new(
        FunnelStepInfo.Search,
        visitId ?? Guid.NewGuid(),
        "fr",
        SalesChannel.WhatsApp,
        Source: "whatsapp",
        UtmCampaign: "ete-2027",
        CategoryCode: "ecmr");

    [Fact]
    public async Task A_step_is_written_in_a_scope_of_its_own_as_the_funnel_actor()
    {
        var world = new RecorderWorld();
        var record = SearchRecord();

        await world.Recorder.RecordAsync(record, Ct);

        var saved = world.Db.Fake<FunnelEvent>().Items.ShouldHaveSingleItem();
        saved.Step.ShouldBe(FunnelStep.Search);
        saved.VisitId.ShouldBe(record.VisitId);
        saved.Channel.ShouldBe(SalesChannel.WhatsApp);
        saved.Source.ShouldBe("whatsapp");
        saved.UtmCampaign.ShouldBe("ete-2027");
        saved.CategoryCode!.Value.ShouldBe("ECMR");
        saved.OccurredAtUtc.ShouldBe(Now);
        world.Db.Saves.ShouldBe(1);
        world.Actors.ShouldBe([FunnelEventRecorder.ActorName]);
    }

    [Fact]
    public async Task An_invalid_record_is_dropped_without_touching_the_database()
    {
        var world = new RecorderWorld();

        await world.Recorder.RecordAsync(SearchRecord() with { Culture = "x" }, Ct);
        await world.Recorder.RecordAsync(null!, Ct);

        world.Db.Fake<FunnelEvent>().Items.ShouldBeEmpty();
        world.Db.Saves.ShouldBe(0);
    }

    [Fact]
    public async Task An_unknown_category_code_is_left_out_but_the_step_is_kept()
    {
        var world = new RecorderWorld();

        await world.Recorder.RecordAsync(SearchRecord() with { CategoryCode = "NOPE!" }, Ct);

        world.Db.Fake<FunnelEvent>().Items.ShouldHaveSingleItem().CategoryCode.ShouldBeNull();
    }

    [Fact]
    public async Task A_database_failure_never_reaches_the_caller()
    {
        var world = new RecorderWorld(failingSave: true);

        await Should.NotThrowAsync(() => world.Recorder.RecordAsync(SearchRecord(), Ct));
    }

    [Fact]
    public async Task An_aborted_request_never_reaches_the_caller()
    {
        var world = new RecorderWorld(failingSave: false, honourCancellation: true);
        using var aborted = new CancellationTokenSource();
        await aborted.CancelAsync();

        await Should.NotThrowAsync(() => world.Recorder.RecordAsync(SearchRecord(), aborted.Token));
        world.Db.Saves.ShouldBe(0);
    }

    [Fact]
    public void The_module_registers_the_recorder_the_purge_and_the_retention_setting()
    {
        var services = new ServiceCollection().AddReportingModule();

        services.ShouldContain(d => d.ServiceType == typeof(IFunnelEvents) && d.ImplementationType == typeof(FunnelEventRecorder) && d.Lifetime == ServiceLifetime.Singleton);
        services.ShouldContain(d => d.ServiceType == typeof(FunnelEventRetentionJob));
        FunnelEventRetentionJob.JobId.ShouldBe("reporting.purge_funnel_events");
        FunnelEventRetentionJob.CronExpression.ShouldBe("45 3 * * *");
        ReportingSettings.FunnelEventRetentionDays.Key.ShouldBe("reporting.funnel_event_retention_days");
        ReportingSettings.FunnelEventRetentionDays.Default.ShouldBe(400);
    }

    private sealed class RecorderWorld
    {
        public RecorderWorld(bool failingSave = false, bool honourCancellation = false)
        {
            var services = new ServiceCollection();
            services.AddScoped<ScopedUserContext>();
            services.AddScoped<IAppDbContext>(provider =>
                new GuardedDbContext(Db, provider.GetRequiredService<ScopedUserContext>(), Actors, failingSave, honourCancellation));
            var provider = services.BuildServiceProvider();
            Recorder = new FunnelEventRecorder(
                provider.GetRequiredService<IServiceScopeFactory>(),
                new FakeTimeProvider(Now),
                NullLogger<FunnelEventRecorder>.Instance);
        }

        public FakeAppDbContext Db { get; } = new();

        public List<string?> Actors { get; } = [];

        public FunnelEventRecorder Recorder { get; }
    }

    /// <summary>
    /// Saves into the shared fake, records the actor of the scope at save time, and can fail or honour the cancellation
    /// token like a real provider.
    /// </summary>
    private sealed class GuardedDbContext(FakeAppDbContext inner, ScopedUserContext userContext, List<string?> actors, bool fail, bool honourCancellation)
        : IAppDbContext
    {
        public DbSet<TEntity> Set<TEntity>()
            where TEntity : class => inner.Set<TEntity>();

        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            if (honourCancellation)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (fail)
            {
                throw new InvalidOperationException("The database is down.");
            }

            actors.Add(userContext.UserName);
            return inner.SaveChangesAsync(cancellationToken);
        }
    }
}
