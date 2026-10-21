using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Channels.Adapters;
using CarRental.Application.Channels.Core;
using CarRental.Application.Channels.Logging;
using CarRental.Application.Channels.Outbound;
using CarRental.Application.Tests.Bookings.Availability;
using CarRental.Domain.Channels;
using CarRental.Domain.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace CarRental.Application.Tests.Channels;

/// <summary>
/// The outbound synchronization on fakes: changes coalesce (debounce), an update is built from the engines and sent
/// through the adapter, a failure is retried with growing delays then fails for good, and a replay sends it again.
/// </summary>
public sealed class ChannelSyncTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Changes_coalesce_until_they_are_drained()
    {
        using var signal = new ChannelSyncSignal();
        var category = Guid.CreateVersion7();

        signal.CategoryChanged(category);
        signal.CategoryChanged(category);
        var first = signal.Drain();
        signal.AllChanged();
        var second = signal.Drain();

        first.Categories.ShouldBe([category]);
        first.All.ShouldBeFalse();
        second.All.ShouldBeTrue();
        signal.Drain().IsEmpty.ShouldBeTrue();
    }

    [Fact]
    public void The_daily_headroom_is_the_smallest_of_the_day_and_runs_merge_equal_days()
    {
        var today = new DateOnly(2027, 7, 1);
        CapacitySegmentInfo[] profile =
        [
            new(Day(today, 0, 3), 3, 1),
            new(Period.Create(TunisTime.FromLocal(today.AddDays(3), new TimeOnly(0, 0)), TunisTime.FromLocal(today.AddDays(3), new TimeOnly(12, 0))).Value, 3, 3),
            new(Period.Create(TunisTime.FromLocal(today.AddDays(3), new TimeOnly(12, 0)), TunisTime.StartOfDay(today.AddDays(5))).Value, 3, 4),
        ];

        var daily = ChannelSyncPayloads.DailyHeadroom(profile, today, today.AddDays(6));
        var runs = ChannelSyncPayloads.Runs(today, daily).ToList();

        daily.ShouldBe([2, 2, 2, 0, 0, 0], "an over-booked day is 0, a day without data too");
        runs.ShouldBe([(today, today.AddDays(2), 2), (today.AddDays(3), today.AddDays(5), 0)]);
    }

    [Fact]
    public async Task A_change_of_a_mapped_category_is_pushed_once_to_the_connected_channel()
    {
        var sync = new SyncWorld();
        sync.Signal.CategoryChanged(sync.CategoryId);
        sync.Signal.CategoryChanged(sync.CategoryId);
        sync.Signal.CategoryChanged(Guid.CreateVersion7());

        var run = await sync.Dispatcher().RunAsync(Ct);

        run.Sent.ShouldBe(1);
        var push = sync.Outbox.ReceivedBy(sync.Channels.Channel.Code).ShouldHaveSingleItem();
        push.Operation.ShouldBe(ChannelSyncOperation.PushAvailability);
        push.Payload.ShouldContain("\"locationCode\":\"TUN\"");
        var item = sync.Channels.Db.Rows<ChannelSyncItem>().ShouldHaveSingleItem();
        item.Status.ShouldBe(ChannelSyncStatus.Sent);
        item.Payload.ShouldNotBeNull().ShouldContain("ECMR");
        sync.Channels.Db.Rows<ChannelMessageLog>().ShouldHaveSingleItem().Direction.ShouldBe(ChannelMessageDirection.Outbound);
    }

    [Fact]
    public async Task Updates_queued_within_the_debounce_window_coalesce_into_one()
    {
        var sync = new SyncWorld();
        var queue = sync.Queue();

        await queue.EnqueueAsync(sync.Channels.Channel.Id, ChannelSyncOperation.PushAvailability, sync.CategoryId, "restriction", Ct);
        await sync.Channels.Db.SaveChangesAsync(Ct);
        await queue.EnqueueAsync(sync.Channels.Channel.Id, ChannelSyncOperation.PushAvailability, sync.CategoryId, "restriction", Ct);
        var early = await sync.Dispatcher().RunAsync(Ct);
        sync.Clock.Advance(SyncWorld.Debounce);
        var due = await sync.Dispatcher().RunAsync(Ct);

        sync.Channels.Db.Rows<ChannelSyncItem>().ShouldHaveSingleItem();
        early.Sent.ShouldBe(0, "still within the debounce window");
        due.Sent.ShouldBe(1);
        sync.Outbox.ReceivedBy(sync.Channels.Channel.Code).Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_refused_push_is_retried_with_growing_delays_then_sent()
    {
        var sync = new SyncWorld();
        sync.Outbox.FailNext(sync.Channels.Channel.Code, 2);
        sync.Signal.CategoryChanged(sync.CategoryId);

        await sync.Dispatcher().RunAsync(Ct);
        var item = sync.Channels.Db.Rows<ChannelSyncItem>().Single();
        var firstRetry = item.NextAttemptAtUtc - sync.Clock.GetUtcNow();
        sync.Clock.Advance(firstRetry);
        await sync.Dispatcher().RunAsync(Ct);
        var secondRetry = item.NextAttemptAtUtc - sync.Clock.GetUtcNow();
        sync.Clock.Advance(secondRetry);
        await sync.Dispatcher().RunAsync(Ct);

        firstRetry.ShouldBe(TimeSpan.FromSeconds(30));
        secondRetry.ShouldBe(TimeSpan.FromMinutes(2));
        item.Status.ShouldBe(ChannelSyncStatus.Sent);
        item.Attempts.ShouldBe(3);
        sync.Channels.Db.Rows<ChannelMessageLog>().Select(l => l.Status).ShouldBe([ChannelMessageStatus.Failed, ChannelMessageStatus.Failed, ChannelMessageStatus.Accepted]);
    }

    [Fact]
    public async Task An_update_that_keeps_failing_fails_for_good_and_a_replay_sends_it_again()
    {
        var sync = new SyncWorld(maxAttempts: 2);
        sync.Outbox.FailNext(sync.Channels.Channel.Code, 5);
        sync.Signal.CategoryChanged(sync.CategoryId);
        await sync.Dispatcher().RunAsync(Ct);
        var item = sync.Channels.Db.Rows<ChannelSyncItem>().Single();
        sync.Clock.Advance(TimeSpan.FromMinutes(1));
        await sync.Dispatcher().RunAsync(Ct);
        item.Status.ShouldBe(ChannelSyncStatus.Failed);
        item.LastError.ShouldNotBeNull().ShouldStartWith("channels.sync.push_failed");

        sync.Outbox.FailNext(sync.Channels.Channel.Code, 0);
        item.Replay(sync.Clock.GetUtcNow());
        await sync.Dispatcher().RunAsync(Ct);

        item.Status.ShouldBe(ChannelSyncStatus.Sent);
        sync.Outbox.ReceivedBy(sync.Channels.Channel.Code).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Updates_of_a_channel_disconnected_meanwhile_are_skipped_and_no_new_one_is_queued()
    {
        var sync = new SyncWorld();
        await sync.Queue().EnqueueAsync(sync.Channels.Channel.Id, ChannelSyncOperation.PushAvailability, sync.CategoryId, "availability", Ct, immediate: true);
        await sync.Channels.Db.SaveChangesAsync(Ct);
        sync.Channels.Channel.ChangeState(ChannelState.Manual, sync.Channels.Stamp).IsSuccess.ShouldBeTrue();
        sync.Signal.AllChanged();

        var run = await sync.Dispatcher().RunAsync(Ct);

        run.Enqueued.ShouldBe(0, "only connected channels get updates");
        run.Skipped.ShouldBe(1);
        sync.Channels.Db.Rows<ChannelSyncItem>().ShouldHaveSingleItem().Status.ShouldBe(ChannelSyncStatus.Skipped);
        sync.Outbox.Received.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_new_published_plan_pushes_the_rates_of_a_connected_channel_and_reminds_a_manual_one()
    {
        var sync = new SyncWorld();
        sync.Pricing.Plan = DailyRatePricing.PlanVersion(sync.Channels.AgencyId, 1);
        await sync.Dispatcher().RunAsync(Ct);
        sync.Outbox.Received.ShouldBeEmpty("the first look only records the plan");

        sync.Pricing.Plan = DailyRatePricing.PlanVersion(sync.Channels.AgencyId, 2);
        await sync.Dispatcher().RunAsync(Ct);
        var rates = sync.Outbox.ReceivedBy(sync.Channels.Channel.Code).ShouldHaveSingleItem();
        rates.Operation.ShouldBe(ChannelSyncOperation.PushRates);
        rates.Payload.ShouldContain("\"dailyPriceInclVat\":120");

        sync.Channels.Channel.ChangeState(ChannelState.Manual, sync.Channels.Stamp).IsSuccess.ShouldBeTrue();
        sync.Pricing.Plan = DailyRatePricing.PlanVersion(sync.Channels.AgencyId, 3);
        var run = await sync.Dispatcher().RunAsync(Ct);

        run.Reminders.ShouldBe(1);
        sync.Channels.Db.Rows<ChannelReplicationItem>().ShouldHaveSingleItem().Detail.ShouldBe("Grille v3");
    }

    private static Period Day(DateOnly first, int fromDay, int toDay) =>
        Period.Create(TunisTime.StartOfDay(first.AddDays(fromDay)), TunisTime.StartOfDay(first.AddDays(toDay))).Value;

    /// <summary>A connected test channel (ECMR at TUN) and the dispatcher on fakes.</summary>
    private sealed class SyncWorld
    {
        public static readonly TimeSpan Debounce = TimeSpan.FromSeconds(10);

        private readonly int _maxAttempts;

        public SyncWorld(int maxAttempts = 8)
        {
            _maxAttempts = maxAttempts;
            Channels = new ChannelWorld(Guid.CreateVersion7(), Clock);
            CategoryId = Fleet.AddCategory(Channels.AgencyId, "ECMR").Id;
            Channels.Channel.MapCategory(CategoryId, "ECMR", null, Channels.Stamp).IsSuccess.ShouldBeTrue();
            Channels.Channel.MapLocation(Guid.CreateVersion7(), "TUN", Channels.Stamp).IsSuccess.ShouldBeTrue();
            Channels.Channel.ChangeState(ChannelState.Connected, Channels.Stamp).IsSuccess.ShouldBeTrue();
            foreach (var mapping in Channels.Channel.Mappings)
            {
                Channels.Db.Set<ChannelMapping>().Add(mapping);
            }

            Pricing.DailyPrices[CategoryId] = 120m;
        }

        public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2027, 6, 1, 8, 0, 0, TimeSpan.Zero));

        public ChannelWorld Channels { get; }

        public Guid CategoryId { get; }

        public FakeFleetQueries Fleet { get; } = new(new CallJournal());

        public ChannelSyncSignal Signal { get; } = new();

        public FakeChannelOutbox Outbox { get; } = new();

        public ProfileAvailability Availability { get; } = new();

        public DailyRatePricing Pricing { get; } = new();

        public IOptions<ChannelSyncOptions> Options => Microsoft.Extensions.Options.Options.Create(new ChannelSyncOptions { Debounce = Debounce, MaxAttempts = _maxAttempts });

        public ChannelSyncQueue Queue() => new(Channels.Db, Clock, Options);

        public ChannelSyncDispatcher Dispatcher() => new(
            Channels.Db,
            Signal,
            Queue(),
            new ChannelSyncPayloads(Availability, Pricing, Channels.Reader(), new FakeSettingsReader(new CallJournal()), Clock),
            new ChannelContextLoader(Channels.Db, Fleet),
            new ChannelAdapterRegistry([new ManualChannelAdapter(), new FakeChannelAdapter(Outbox)]),
            Pricing,
            new ChannelMessageLogger(Channels.Provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<ChannelMessageLogger>.Instance),
            Clock,
            Options,
            NullLogger<ChannelSyncDispatcher>.Instance);
    }
}
