using CarRental.Application.Bookings.Availability;
using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Common.Availability;
using CarRental.Application.Common.Security;
using CarRental.Domain.Bookings.Availability;
using CarRental.Domain.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using static CarRental.Application.Tests.Bookings.Availability.AvailabilityWorld;

namespace CarRental.Application.Tests.Bookings.Availability;

/// <summary>In-memory snapshots per category: single flight, invalidation by notification, short absolute expiry.</summary>
public sealed class AvailabilityCacheTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<CategoryAvailabilityInfo?> ReadAsync(
        ServiceProvider services,
        Guid categoryId,
        Period rental,
        CancellationToken? cancellationToken = null)
    {
        await using var scope = services.CreateAsyncScope();
        var queries = scope.ServiceProvider.GetRequiredService<IAvailabilityQueries>();
        return await queries.GetCategoryAvailabilityAsync(categoryId, rental, SalesChannel.Web, cancellationToken: cancellationToken ?? Ct);
    }

    [Fact]
    public async Task A_snapshot_is_loaded_once_then_served_from_memory()
    {
        var world = new AvailabilityWorld();
        var category = world.Category(vehicles: 2);
        await using var services = world.Services();

        for (var i = 0; i < 5; i++)
        {
            (await ReadAsync(services, category.Id, P(10 + i, 20 + i)))!.IsAvailable.ShouldBeTrue();
        }

        services.GetRequiredService<AvailabilityCache>().SnapshotLoads.ShouldBe(1);
        world.Occupancy.Reads.ShouldBe(1);
        world.Fleet.AvailabilityReads.ShouldBe(1);
    }

    [Fact]
    public async Task Concurrent_cold_reads_share_a_single_load()
    {
        var world = new AvailabilityWorld();
        var category = world.Category(vehicles: 2);
        world.Fleet.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var services = world.Services();

        var readers = Enumerable.Range(0, 32).Select(_ => ReadAsync(services, category.Id, P(10, 20))).ToList();
        await world.Fleet.CategoryReadStarted.Task.WaitAsync(Ct);
        world.Fleet.Gate.SetResult();
        var results = await Task.WhenAll(readers);

        results.ShouldAllBe(result => result!.IsAvailable && result.Remaining == 2);
        world.Fleet.CategoryReads.ShouldBe(1);
        world.Occupancy.Reads.ShouldBe(1);
        services.GetRequiredService<AvailabilityCache>().SnapshotLoads.ShouldBe(1);
    }

    [Fact]
    public async Task A_reader_that_gives_up_does_not_cancel_the_shared_load()
    {
        var world = new AvailabilityWorld();
        var category = world.Category(vehicles: 1);
        world.Fleet.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var services = world.Services();
        using var impatient = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        var first = ReadAsync(services, category.Id, P(10, 20), impatient.Token);
        await world.Fleet.CategoryReadStarted.Task.WaitAsync(Ct);
        var second = ReadAsync(services, category.Id, P(10, 20));
        await impatient.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(() => first);
        world.Fleet.Gate.SetResult();

        (await second)!.IsAvailable.ShouldBeTrue();
        services.GetRequiredService<AvailabilityCache>().SnapshotLoads.ShouldBe(1);
    }

    [Fact]
    public async Task A_change_notification_reloads_only_that_category()
    {
        var world = new AvailabilityWorld();
        var changed = world.Category("ECMR", vehicles: 1);
        var untouched = world.Category("CDMR", vehicles: 1);
        await using var services = world.Services();
        await ReadAsync(services, changed.Id, P(10, 20));
        await ReadAsync(services, untouched.Id, P(10, 20));

        // A booking commits, then its writer notifies (Bookings module).
        world.Book(changed, P(10, 20));
        services.GetRequiredService<IAvailabilityChangeNotifier>().CategoryChanged(changed.Id);

        (await ReadAsync(services, changed.Id, P(10, 20)))!.IsAvailable.ShouldBeFalse();
        (await ReadAsync(services, untouched.Id, P(10, 20)))!.IsAvailable.ShouldBeTrue();
        services.GetRequiredService<AvailabilityCache>().SnapshotLoads.ShouldBe(3);
    }

    [Fact]
    public async Task A_global_change_reloads_every_category()
    {
        var world = new AvailabilityWorld();
        var first = world.Category("ECMR");
        var second = world.Category("CDMR");
        await using var services = world.Services();
        await ReadAsync(services, first.Id, P(10, 20));
        await ReadAsync(services, second.Id, P(10, 20));

        // e.g. the preparation buffer setting changed.
        world.Settings.BufferByAgency[world.AgencyId] = TimeSpan.FromHours(6);
        services.GetRequiredService<IAvailabilityChangeNotifier>().AllChanged();
        await ReadAsync(services, first.Id, P(10, 20));
        await ReadAsync(services, second.Id, P(10, 20));

        services.GetRequiredService<AvailabilityCache>().SnapshotLoads.ShouldBe(4);
        world.Occupancy.LastBasis!.PreparationBuffer.ShouldBe(TimeSpan.FromHours(6));
    }

    [Fact]
    public async Task A_notification_during_a_load_is_not_lost()
    {
        var world = new AvailabilityWorld();
        var category = world.Category(vehicles: 1);
        world.Occupancy.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var services = world.Services();

        // The load reads the bookings, then a booking commits and is notified before the load completes.
        var early = ReadAsync(services, category.Id, P(10, 20));
        await world.Occupancy.ReadTaken.Task.WaitAsync(Ct);
        world.Book(category, P(10, 20));
        services.GetRequiredService<IAvailabilityChangeNotifier>().CategoryChanged(category.Id);
        world.Occupancy.Gate.SetResult();

        (await early)!.IsAvailable.ShouldBeTrue();
        (await ReadAsync(services, category.Id, P(10, 20)))!.IsAvailable.ShouldBeFalse();
        services.GetRequiredService<AvailabilityCache>().SnapshotLoads.ShouldBe(2);
    }

    [Fact]
    public async Task Without_notification_a_snapshot_is_reloaded_after_the_absolute_expiration()
    {
        var world = new AvailabilityWorld();
        var category = world.Category(vehicles: 1);
        await using var services = world.Services();
        await ReadAsync(services, category.Id, P(10, 20));
        world.Book(category, P(10, 20));

        world.Clock.Advance(TimeSpan.FromMinutes(1));
        (await ReadAsync(services, category.Id, P(10, 20)))!.IsAvailable.ShouldBeTrue();

        world.Clock.Advance(TimeSpan.FromMinutes(1));
        (await ReadAsync(services, category.Id, P(10, 20)))!.IsAvailable.ShouldBeFalse();
        services.GetRequiredService<AvailabilityCache>().SnapshotLoads.ShouldBe(2);
    }

    [Fact]
    public async Task The_expiration_can_be_configured()
    {
        var world = new AvailabilityWorld();
        var category = world.Category();
        await using var services = world.Services(s => s.Configure<AvailabilityCacheOptions>(o => o.AbsoluteExpiration = TimeSpan.FromSeconds(10)));
        await ReadAsync(services, category.Id, P(10, 20));

        world.Clock.Advance(TimeSpan.FromSeconds(10));
        await ReadAsync(services, category.Id, P(10, 20));

        services.GetRequiredService<AvailabilityCache>().SnapshotLoads.ShouldBe(2);
    }

    [Fact]
    public async Task A_failed_load_is_not_kept()
    {
        var world = new AvailabilityWorld();
        var category = world.Category();
        world.Fleet.NextAvailabilityFailure = new InvalidOperationException("Simulated SQL outage.");
        await using var services = world.Services();

        await Should.ThrowAsync<InvalidOperationException>(() => ReadAsync(services, category.Id, P(10, 20)));
        (await ReadAsync(services, category.Id, P(10, 20)))!.IsAvailable.ShouldBeTrue();

        services.GetRequiredService<AvailabilityCache>().SnapshotLoads.ShouldBe(2);
    }

    [Fact]
    public async Task Periods_beyond_the_cached_horizon_are_read_fresh_and_not_kept()
    {
        var world = new AvailabilityWorld();
        var category = world.Category(vehicles: 1);
        await using var services = world.Services();
        var farAway = P(500 * 24, (500 * 24) + 48);
        world.Book(category, farAway);
        await ReadAsync(services, category.Id, P(10, 20));

        (await ReadAsync(services, category.Id, farAway))!.IsAvailable.ShouldBeFalse();
        (await ReadAsync(services, category.Id, farAway))!.IsAvailable.ShouldBeFalse();

        services.GetRequiredService<AvailabilityCache>().SnapshotLoads.ShouldBe(1);
        world.Fleet.AvailabilityReads.ShouldBe(3);
        world.Occupancy.LastRange.ShouldBe(farAway.ExtendEnd(TimeSpan.FromHours(2)));
    }

    [Fact]
    public async Task The_snapshot_covers_yesterday_to_four_hundred_days_ahead_in_tunis_time()
    {
        var world = new AvailabilityWorld();
        var category = world.Category();
        await using var services = world.Services();
        await ReadAsync(services, category.Id, P(10, 20));

        // Now: 1 July 2027 09:00 in Tunis. Yesterday starts on 30 June 00:00 Tunis (29 June 23:00 UTC).
        world.Occupancy.LastRange.ShouldBe(Period.Create(
            new DateTimeOffset(2027, 6, 29, 23, 0, 0, TimeSpan.Zero),
            TunisTime.StartOfDay(new DateOnly(2027, 7, 1).AddDays(401))).Value);
    }

    [Fact]
    public async Task Loads_run_as_the_system_actor()
    {
        var world = new AvailabilityWorld();
        var category = world.Category();
        string? actor = null;
        await using var services = world.Services(s =>
        {
            s.AddScoped<ScopedUserContext>();
            s.AddScoped<IOccupancyReader>(provider => new ActorRecordingReader(provider.GetRequiredService<ScopedUserContext>(), world.Occupancy, name => actor = name));
        });

        await ReadAsync(services, category.Id, P(10, 20));

        actor.ShouldBe(AvailabilityCache.ActorName);
    }

    [Fact]
    public async Task The_category_list_of_an_agency_is_cached_and_reloaded_after_a_change()
    {
        var world = new AvailabilityWorld();
        world.Category("ECMR");
        await using var services = world.Services();
        var cache = services.GetRequiredService<AvailabilityCache>();

        async Task<int> SearchAsync()
        {
            await using var scope = services.CreateAsyncScope();
            var queries = scope.ServiceProvider.GetRequiredService<IAvailabilityQueries>();
            return (await queries.GetAvailableCategoriesAsync(world.AgencyId, P(10, 20), SalesChannel.Web, Ct)).Count;
        }

        (await SearchAsync()).ShouldBe(1);
        var added = world.Category("CDMR");
        (await SearchAsync()).ShouldBe(1);
        cache.CategoryListLoads.ShouldBe(1);

        // The Fleet module notifies the new category: the list reloads.
        services.GetRequiredService<IAvailabilityChangeNotifier>().CategoryChanged(added.Id);
        (await SearchAsync()).ShouldBe(2);
        cache.CategoryListLoads.ShouldBe(2);
    }

    [Fact]
    public async Task A_category_deleted_before_a_fresh_read_beyond_the_horizon_is_reported_as_missing()
    {
        var world = new AvailabilityWorld();
        var category = world.Category();
        await using var services = world.Services();
        await using var scope = services.CreateAsyncScope();
        var queries = scope.ServiceProvider.GetRequiredService<IAvailabilityQueries>();
        (await queries.GetCategoryAvailabilityAsync(category.Id, P(10, 20), SalesChannel.Web, cancellationToken: Ct)).ShouldNotBeNull();

        world.Fleet.RemoveCategory(category.Id);

        (await queries.GetCategoryAvailabilityAsync(category.Id, P(500 * 24, (500 * 24) + 5), SalesChannel.Web, cancellationToken: Ct)).ShouldBeNull();
        (await queries.GetCapacityProfileAsync(category.Id, P(-72, 0), cancellationToken: Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task Concurrent_readers_never_go_back_to_an_older_snapshot()
    {
        // Eight readers race a writer that commits three bookings, each followed by its notification. Bookings only
        // accumulate, so every reader must see a non-increasing number of vehicles left, and the last state at the end.
        var world = new AvailabilityWorld();
        var category = world.Category(vehicles: 3);
        await using var services = world.Services();
        var notifier = services.GetRequiredService<IAvailabilityChangeNotifier>();
        var writerDone = 0;

        var readers = Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            var seen = new List<int>();
            for (var i = 0; i < 400 || Volatile.Read(ref writerDone) == 0; i++)
            {
                seen.Add((await ReadAsync(services, category.Id, P(10, 20)))!.Remaining);
            }

            return seen;
        }, Ct)).ToList();
        var writer = Task.Run(
            async () =>
            {
                for (var booking = 0; booking < 3; booking++)
                {
                    await Task.Delay(5, Ct);
                    world.Book(category, P(0, 30));
                    notifier.CategoryChanged(category.Id);
                }

                Volatile.Write(ref writerDone, 1);
            },
            Ct);

        await writer;
        var observed = await Task.WhenAll(readers);

        foreach (var seen in observed)
        {
            seen.Zip(seen.Skip(1)).ShouldAllBe(pair => pair.Second <= pair.First);
        }

        (await ReadAsync(services, category.Id, P(10, 20)))!.ShouldBe(new CategoryAvailabilityInfo(category.Id, false, 0, P(10, 22)));
    }

    [Fact]
    public async Task Unknown_ids_are_not_kept_in_the_cache()
    {
        // Arbitrary ids reaching the read side must not grow the cache: each one is read again, never stored.
        var world = new AvailabilityWorld();
        await using var services = world.Services();
        var cache = services.GetRequiredService<AvailabilityCache>();
        var unknownCategory = Guid.CreateVersion7();
        var unknownAgency = Guid.CreateVersion7();

        for (var i = 0; i < 2; i++)
        {
            await using var scope = services.CreateAsyncScope();
            var queries = scope.ServiceProvider.GetRequiredService<IAvailabilityQueries>();
            (await queries.GetCategoryAvailabilityAsync(unknownCategory, P(10, 20), SalesChannel.Web, cancellationToken: Ct)).ShouldBeNull();
            (await queries.GetAvailableCategoriesAsync(unknownAgency, P(10, 20), SalesChannel.Web, Ct)).ShouldBeEmpty();
        }

        cache.SnapshotLoads.ShouldBe(2);
        cache.CategoryListLoads.ShouldBe(2);
    }

    [Fact]
    public async Task Unknown_categories_are_reported_as_missing()
    {
        var world = new AvailabilityWorld();
        await using var services = world.Services();
        await using var scope = services.CreateAsyncScope();
        var queries = scope.ServiceProvider.GetRequiredService<IAvailabilityQueries>();

        (await queries.GetCategoryAvailabilityAsync(Guid.CreateVersion7(), P(10, 20), SalesChannel.Web, cancellationToken: Ct)).ShouldBeNull();
        (await queries.GetCapacityProfileAsync(Guid.CreateVersion7(), P(10, 20), cancellationToken: Ct)).ShouldBeNull();
    }

    [Fact]
    public void Invalid_options_are_rejected()
    {
        var world = new AvailabilityWorld();
        using var services = new ServiceCollection().BuildServiceProvider();
        var scopes = services.GetRequiredService<IServiceScopeFactory>();

        AvailabilityCacheOptions[] invalid =
        [
            new() { AbsoluteExpiration = TimeSpan.Zero },
            new() { HorizonDaysAfter = 0 },
            new() { HorizonDaysBefore = -1 },
        ];

        foreach (var options in invalid)
        {
            Should.Throw<ArgumentException>(() => new AvailabilityCache(scopes, world.Clock, Options.Create(options), NullLogger<AvailabilityCache>.Instance));
        }
    }

    /// <summary>Records the actor of the scope the cache loads in.</summary>
    private sealed class ActorRecordingReader(IUserContext userContext, IOccupancyReader inner, Action<string?> record) : IOccupancyReader
    {
        public Task<IReadOnlyList<OccupancyInterval>> GetOccupancyAsync(
            Guid categoryId,
            Period range,
            Guid? excludedBookingId,
            OccupancyBasis basis,
            CancellationToken cancellationToken = default)
        {
            record(userContext.IsSystem ? userContext.UserName : null);
            return inner.GetOccupancyAsync(categoryId, range, excludedBookingId, basis, cancellationToken);
        }
    }
}
