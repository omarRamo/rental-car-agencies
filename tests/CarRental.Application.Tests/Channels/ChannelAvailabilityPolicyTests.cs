using CarRental.Application.Bookings.Availability;
using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Tests.Bookings.Availability;
using CarRental.Domain.Bookings.Availability;
using CarRental.Domain.Channels;
using CarRental.Domain.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using static CarRental.Application.Tests.Bookings.Availability.AvailabilityWorld;

namespace CarRental.Application.Tests.Channels;

/// <summary>
/// The quotas and stop-sells of a channel through the real availability engine (calculator, cache, guard) on fakes: they
/// restrict their channel only, every reader and writer sees them, and a write decides on fresh data.
/// </summary>
public sealed class ChannelAvailabilityPolicyTests
{
    private static readonly DateOnly Today = TunisTime.DateOf(Now);

    private static Period Rental(int fromDay, int days) =>
        Period.Create(TunisTime.FromLocal(Today.AddDays(fromDay), new TimeOnly(10, 0)), TunisTime.FromLocal(Today.AddDays(fromDay + days), new TimeOnly(10, 0))).Value;

    [Fact]
    public async Task A_stop_sell_on_booking_com_never_blocks_the_website()
    {
        var engine = new Engine();
        var category = engine.World.Category("ECMR", vehicles: 3);
        engine.Channels.StopSell(category.Id, Today.AddDays(5), Today.AddDays(7));
        await using var scope = engine.Services.CreateAsyncScope();
        var availability = scope.ServiceProvider.GetRequiredService<IAvailabilityQueries>();

        var website = await availability.GetCategoryAvailabilityAsync(category.Id, Rental(5, 2), SalesChannel.Web, cancellationToken: TestContext.Current.CancellationToken);
        var phone = await availability.GetCategoryAvailabilityAsync(category.Id, Rental(5, 2), SalesChannel.Phone, cancellationToken: TestContext.Current.CancellationToken);
        var bookingCom = await availability.GetCategoryAvailabilityAsync(category.Id, Rental(5, 2), SalesChannel.BookingCom, cancellationToken: TestContext.Current.CancellationToken);
        var afterTheStop = await availability.GetCategoryAvailabilityAsync(category.Id, Rental(9, 2), SalesChannel.BookingCom, cancellationToken: TestContext.Current.CancellationToken);

        website!.IsAvailable.ShouldBeTrue();
        website.Remaining.ShouldBe(3);
        phone!.Remaining.ShouldBe(3);
        bookingCom!.IsAvailable.ShouldBeFalse();
        bookingCom.Remaining.ShouldBe(0);
        afterTheStop!.Remaining.ShouldBe(3);
    }

    [Fact]
    public async Task A_quota_keeps_vehicles_away_from_the_channel_and_counts_every_booking()
    {
        var engine = new Engine();
        var category = engine.World.Category("ECMR", vehicles: 4);
        engine.Channels.Reserve(category.Id, Today, Today.AddDays(30), vehicles: 2);
        engine.World.Book(category, Rental(10, 3));
        await using var scope = engine.Services.CreateAsyncScope();
        var availability = scope.ServiceProvider.GetRequiredService<IAvailabilityQueries>();

        var website = await availability.GetCategoryAvailabilityAsync(category.Id, Rental(10, 2), SalesChannel.Web, cancellationToken: TestContext.Current.CancellationToken);
        var bookingCom = await availability.GetCategoryAvailabilityAsync(category.Id, Rental(10, 2), SalesChannel.BookingCom, cancellationToken: TestContext.Current.CancellationToken);
        var bookingComLater = await availability.GetCategoryAvailabilityAsync(category.Id, Rental(40, 2), SalesChannel.BookingCom, cancellationToken: TestContext.Current.CancellationToken);
        var available = await availability.GetAvailableCategoriesAsync(engine.World.AgencyId, Rental(10, 2), SalesChannel.BookingCom, TestContext.Current.CancellationToken);

        website!.Remaining.ShouldBe(3, "4 vehicles, 1 booked");
        bookingCom!.Remaining.ShouldBe(1, "4 − 2 kept for the other channels − 1 booked");
        bookingComLater!.Remaining.ShouldBe(4, "the quota ended");
        available.ShouldHaveSingleItem().Remaining.ShouldBe(1);
    }

    [Fact]
    public async Task A_stop_sell_of_one_location_is_left_to_the_channel_module()
    {
        var engine = new Engine();
        var category = engine.World.Category("ECMR", vehicles: 2);
        engine.Channels.StopSell(category.Id, Today, Today.AddDays(3), locationId: Guid.CreateVersion7());
        await using var scope = engine.Services.CreateAsyncScope();

        var bookingCom = await scope.ServiceProvider.GetRequiredService<IAvailabilityQueries>()
            .GetCategoryAvailabilityAsync(category.Id, Rental(1, 1), SalesChannel.BookingCom, cancellationToken: TestContext.Current.CancellationToken);

        bookingCom!.Remaining.ShouldBe(2, "the engine has no location: the Channels module refuses at that location");
    }

    [Fact]
    public async Task The_writers_guard_reads_the_restrictions_fresh_and_refuses_the_channel_only()
    {
        var engine = new Engine();
        var category = engine.World.Category("ECMR", vehicles: 1);
        _ = engine.Channels.Policy();
        var loader = new AvailabilitySnapshotLoader(engine.World.Fleet, engine.World.Occupancy, engine.Channels.Policy(), engine.World.Settings, engine.World.Clock);
        var guard = new AvailabilityGuard(engine.Channels.UnitOfWork, new FakeAvailabilityLock(engine.Channels.UnitOfWork, engine.World.Journal), loader, engine.World.Fleet);

        // Added after the cache was built and without telling it: only a fresh read sees it.
        engine.Channels.Db.Set<ChannelQuota>().Add(ChannelQuota.StopSell(engine.Channels.Channel, category.Id, null, Today.AddDays(2), Today.AddDays(4), null, engine.Channels.Stamp).Value);
        var request = new BookingOccupancy(Guid.CreateVersion7(), OccupancyKind.Planned, Rental(2, 1));

        var bookingCom = await engine.Channels.UnitOfWork.ExecuteAsync(ct => guard.EnsureAvailableAsync(new AvailabilityRequest(category.Id, SalesChannel.BookingCom, request), cancellationToken: ct), cancellationToken: TestContext.Current.CancellationToken);
        var manualEntry = await engine.Channels.UnitOfWork.ExecuteAsync(
            ct => guard.EnsureAvailableAsync(new AvailabilityRequest(category.Id, SalesChannel.BookingCom, request) { IgnoreChannelRestrictions = true }, cancellationToken: ct),
            cancellationToken: TestContext.Current.CancellationToken);
        var website = await engine.Channels.UnitOfWork.ExecuteAsync(ct => guard.EnsureAvailableAsync(new AvailabilityRequest(category.Id, SalesChannel.Web, request), cancellationToken: ct), cancellationToken: TestContext.Current.CancellationToken);

        bookingCom.Error.ShouldBe(AvailabilityErrors.NotAvailable);
        manualEntry.IsSuccess.ShouldBeTrue("a booking already sold on the extranet only needs the physical capacity");
        website.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task An_inactive_channel_restricts_nothing()
    {
        var engine = new Engine();
        var category = engine.World.Category("ECMR", vehicles: 2);
        engine.Channels.StopSell(category.Id, Today, Today.AddDays(10));
        engine.Channels.Channel.ChangeState(ChannelState.Inactive, engine.Channels.Stamp).IsSuccess.ShouldBeTrue();
        engine.Channels.Cache.OnCommitted(new HashSet<Type> { typeof(Channel) });

        var restrictions = await engine.Channels.Policy().GetRestrictionsAsync(category.Id, SalesChannel.BookingCom, Rental(1, 2), TestContext.Current.CancellationToken);

        restrictions.ShouldBe(ChannelRestrictionsInfo.None);
    }

    [Fact]
    public async Task A_committed_restriction_drops_the_cached_rules_and_every_availability_snapshot()
    {
        var engine = new Engine();
        var category = engine.World.Category("ECMR", vehicles: 2);
        var policy = engine.Channels.Policy();
        (await policy.GetRestrictionsAsync(category.Id, SalesChannel.BookingCom, Rental(1, 2), TestContext.Current.CancellationToken)).ShouldBe(ChannelRestrictionsInfo.None);
        var notified = engine.Channels.Notifier.All;

        engine.Channels.StopSell(category.Id, Today, Today.AddDays(3));
        var after = await policy.GetRestrictionsAsync(category.Id, SalesChannel.BookingCom, Rental(1, 2), TestContext.Current.CancellationToken);
        engine.Channels.Cache.OnCommitted(new HashSet<Type> { typeof(string) });

        after.StopSales.ShouldHaveSingleItem().ShouldBe(Rental(1, 2), "clipped to the range asked");
        engine.Channels.Notifier.All.ShouldBe(notified + 1, "another entity type does not reload the snapshots");
    }

    /// <summary>The availability engine on fakes with the policy of the Channels module.</summary>
    private sealed class Engine
    {
        public Engine()
        {
            Channels = new ChannelWorld(World.AgencyId, World.Clock, World.Fleet);
            Services = World.Services(services => services.Replace(ServiceDescriptor.Singleton<IChannelAvailabilityPolicy>(Channels.Policy())));
        }

        public AvailabilityWorld World { get; } = new();

        public ChannelWorld Channels { get; }

        public ServiceProvider Services { get; }
    }
}
