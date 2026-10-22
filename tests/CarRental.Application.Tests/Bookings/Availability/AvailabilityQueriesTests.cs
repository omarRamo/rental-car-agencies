using CarRental.Application.Bookings.Availability;
using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Common.Security;
using CarRental.Domain.Bookings.Availability;
using CarRental.Domain.Common;
using Microsoft.Extensions.DependencyInjection;
using static CarRental.Application.Tests.Bookings.Availability.AvailabilityWorld;

namespace CarRental.Application.Tests.Bookings.Availability;

/// <summary>Public read side: search results, category checks and the planning profile.</summary>
public sealed class AvailabilityQueriesTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Available_categories_are_listed_in_display_order_with_the_vehicles_left()
    {
        var world = new AvailabilityWorld();
        var economy = world.Category("ECMR", vehicles: 2, displayOrder: 2);
        var mini = world.Category("MCMR", vehicles: 1, displayOrder: 1);
        var compact = world.Category("CDMR", vehicles: 3, displayOrder: 3);
        world.Category("IFMR", vehicles: 5, displayOrder: 0, isActive: false);
        world.Fleet.AddCategory(Guid.CreateVersion7(), "SDMR");
        world.Book(economy, P(0, 30));
        world.Book(mini, P(15, 16));
        await using var services = world.Services();
        await using var scope = services.CreateAsyncScope();
        var queries = scope.ServiceProvider.GetRequiredService<IAvailabilityQueries>();

        var available = await queries.GetAvailableCategoriesAsync(world.AgencyId, P(10, 20), SalesChannel.Web, Ct);

        available.Select(a => (a.Category.AcrissCode, a.Remaining)).ShouldBe([("ECMR", 1), ("CDMR", 3)]);
        available[0].Category.ShouldBe(economy);
    }

    [Fact]
    public async Task Each_channel_sees_its_own_quotas_and_stop_sales()
    {
        var world = new AvailabilityWorld();
        var category = world.Category(vehicles: 3);
        world.Channels.Set(category.Id, SalesChannel.BookingCom, new ChannelRestrictionsInfo([new ReservedVehiclesInfo(P(0, 1000), 2)], []));
        world.Channels.Set(category.Id, SalesChannel.WhatsApp, new ChannelRestrictionsInfo([], [P(0, 1000)]));
        await using var services = world.Services();
        await using var scope = services.CreateAsyncScope();
        var queries = scope.ServiceProvider.GetRequiredService<IAvailabilityQueries>();

        (await queries.GetAvailableCategoriesAsync(world.AgencyId, P(10, 20), SalesChannel.Web, Ct)).ShouldHaveSingleItem().Remaining.ShouldBe(3);
        (await queries.GetAvailableCategoriesAsync(world.AgencyId, P(10, 20), SalesChannel.BookingCom, Ct)).ShouldHaveSingleItem().Remaining.ShouldBe(1);
        (await queries.GetAvailableCategoriesAsync(world.AgencyId, P(10, 20), SalesChannel.WhatsApp, Ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_category_check_reports_the_first_conflict_and_can_leave_out_the_amended_booking()
    {
        var world = new AvailabilityWorld();
        var category = world.Category(vehicles: 1);
        var bookingId = world.Book(category, P(10, 20));
        await using var services = world.Services();
        await using var scope = services.CreateAsyncScope();
        var queries = scope.ServiceProvider.GetRequiredService<IAvailabilityQueries>();

        var taken = await queries.GetCategoryAvailabilityAsync(category.Id, P(5, 15), SalesChannel.Phone, cancellationToken: Ct);
        var amended = await queries.GetCategoryAvailabilityAsync(category.Id, P(5, 15), SalesChannel.Phone, bookingId, Ct);

        taken.ShouldBe(new CategoryAvailabilityInfo(category.Id, IsAvailable: false, Remaining: 0, FirstConflict: P(10, 17)));
        amended.ShouldBe(new CategoryAvailabilityInfo(category.Id, IsAvailable: true, Remaining: 1, FirstConflict: null));
    }

    [Fact]
    public async Task Only_the_future_part_of_a_period_counts_and_a_past_period_is_not_sellable()
    {
        var world = new AvailabilityWorld();
        var category = world.Category(vehicles: 1);
        world.Book(category, P(-30, -10));
        await using var services = world.Services();
        await using var scope = services.CreateAsyncScope();
        var queries = scope.ServiceProvider.GetRequiredService<IAvailabilityQueries>();

        var startedAnHourAgo = await queries.GetCategoryAvailabilityAsync(category.Id, P(-20, 10), SalesChannel.Counter, cancellationToken: Ct);
        var past = await queries.GetCategoryAvailabilityAsync(category.Id, P(-20, -5), SalesChannel.Counter, cancellationToken: Ct);

        startedAnHourAgo!.IsAvailable.ShouldBeTrue();
        past.ShouldBe(new CategoryAvailabilityInfo(category.Id, IsAvailable: false, Remaining: 0, FirstConflict: P(-20, -3)));
    }

    [Fact]
    public async Task The_preparation_buffer_of_the_agency_applies_to_cached_reads()
    {
        var world = new AvailabilityWorld();
        var category = world.Category(vehicles: 1);
        world.Book(category, P(10, 20));
        world.Settings.BufferByAgency[world.AgencyId] = TimeSpan.FromHours(5);
        await using var services = world.Services();
        await using var scope = services.CreateAsyncScope();
        var queries = scope.ServiceProvider.GetRequiredService<IAvailabilityQueries>();

        (await queries.GetCategoryAvailabilityAsync(category.Id, P(24, 30), SalesChannel.Web, cancellationToken: Ct))!.FirstConflict.ShouldBe(P(24, 25));
        (await queries.GetCategoryAvailabilityAsync(category.Id, P(25, 30), SalesChannel.Web, cancellationToken: Ct))!.IsAvailable.ShouldBeTrue();
    }

    [Fact]
    public async Task The_capacity_profile_describes_the_planning_of_the_whole_fleet_or_of_a_channel()
    {
        var world = new AvailabilityWorld();
        var category = world.Category(vehicles: 1);
        var inMaintenance = world.Fleet.AddVehicle(category, "999 TU 9999", [Always]);
        world.Fleet.SetWindows(inMaintenance.Id, P(-1000, 20), P(26, 1000));
        world.Book(category, P(0, 8));
        world.Book(category, P(5, 28));
        world.Channels.Set(category.Id, SalesChannel.BookingCom, new ChannelRestrictionsInfo([], [P(30, 35)]));
        await using var services = world.Services();
        await using var scope = services.CreateAsyncScope();
        var queries = scope.ServiceProvider.GetRequiredService<IAvailabilityQueries>();

        var planning = await queries.GetCapacityProfileAsync(category.Id, P(0, 40), cancellationToken: Ct);
        var bookingCom = await queries.GetCapacityProfileAsync(category.Id, P(28, 40), SalesChannel.BookingCom, Ct);

        planning.ShouldBe(
        [
            new CapacitySegmentInfo(P(0, 5), 2, 1),
            new CapacitySegmentInfo(P(5, 10), 2, 2),
            new CapacitySegmentInfo(P(10, 20), 2, 1),
            new CapacitySegmentInfo(P(20, 26), 1, 1),
            new CapacitySegmentInfo(P(26, 30), 2, 1),
            new CapacitySegmentInfo(P(30, 40), 2, 0),
        ]);
        planning!.Select(s => s.Remaining).ShouldBe([1, 0, 1, 0, 1, 2]);
        bookingCom.ShouldBe(
        [
            new CapacitySegmentInfo(P(28, 30), 2, 1),
            new CapacitySegmentInfo(P(30, 35), 0, 0),
            new CapacitySegmentInfo(P(35, 40), 2, 0),
        ]);
    }

    [Fact]
    public async Task A_profile_reaching_before_the_cached_horizon_is_read_fresh()
    {
        var world = new AvailabilityWorld();
        var category = world.Category(vehicles: 1);
        world.Book(category, P(-60, -50));
        await using var services = world.Services();
        await using var scope = services.CreateAsyncScope();
        var queries = scope.ServiceProvider.GetRequiredService<IAvailabilityQueries>();

        var lastDays = await queries.GetCapacityProfileAsync(category.Id, P(-72, 0), cancellationToken: Ct);

        lastDays.ShouldBe(
        [
            new CapacitySegmentInfo(P(-72, -60), 1, 0),
            new CapacitySegmentInfo(P(-60, -48), 1, 1),
            new CapacitySegmentInfo(P(-48, 0), 1, 0),
        ]);
        world.Occupancy.LastRange.ShouldBe(P(-72, 0));
    }

    [Fact]
    public async Task The_guard_of_the_writers_never_reads_the_cache()
    {
        var world = new AvailabilityWorld();
        var category = world.Category(vehicles: 1);
        await using var services = world.Services();
        await using var scope = services.CreateAsyncScope();
        var queries = scope.ServiceProvider.GetRequiredService<IAvailabilityQueries>();
        var guard = scope.ServiceProvider.GetRequiredService<IAvailabilityGuard>();
        (await queries.GetCategoryAvailabilityAsync(category.Id, P(10, 20), SalesChannel.Web, cancellationToken: Ct))!.IsAvailable.ShouldBeTrue();

        // Another writer committed the last vehicle and has not notified yet: the cache is stale, the guard is not.
        world.Book(category, P(0, 30));
        var request = new AvailabilityRequest(category.Id, SalesChannel.Web, Planned(Guid.CreateVersion7(), P(10, 20)));
        var write = await world.UnitOfWork.ExecuteAsync(ct => guard.EnsureAvailableAsync(request, cancellationToken: ct), cancellationToken: Ct);

        (await queries.GetCategoryAvailabilityAsync(category.Id, P(10, 20), SalesChannel.Web, cancellationToken: Ct))!.IsAvailable.ShouldBeTrue();
        write.Error.ShouldBe(AvailabilityErrors.NotAvailable);
    }

    [Fact]
    public async Task An_operation_of_another_agency_never_reads_the_cached_availability_of_an_agency()
    {
        // The cache is loaded across agencies (ADR-0067): an operation limited to an agency is answered for its own
        // categories only, as if those of another agency did not exist — beyond the cached horizon too.
        var world = new AvailabilityWorld();
        var category = world.Category("ECMR", vehicles: 2);
        await using var services = world.Services();
        world.Actor = UserContextSnapshot.System("system:tests", agencyId: Guid.CreateVersion7());
        await using (var other = services.CreateAsyncScope())
        {
            var queries = other.ServiceProvider.GetRequiredService<IAvailabilityQueries>();
            (await queries.GetAvailableCategoriesAsync(world.AgencyId, P(10, 20), SalesChannel.Web, Ct)).ShouldBeEmpty();
            (await queries.GetCategoryAvailabilityAsync(category.Id, P(10, 20), SalesChannel.Web, cancellationToken: Ct)).ShouldBeNull();
            (await queries.GetCategoryAvailabilityAsync(category.Id, P(24 * 500, 24 * 501), SalesChannel.Web, cancellationToken: Ct)).ShouldBeNull();
            (await queries.GetCapacityProfileAsync(category.Id, P(10, 20), cancellationToken: Ct)).ShouldBeNull();
        }

        world.Actor = UserContextSnapshot.System("system:tests", agencyId: world.AgencyId);
        await using var own = services.CreateAsyncScope();
        var ownQueries = own.ServiceProvider.GetRequiredService<IAvailabilityQueries>();
        (await ownQueries.GetAvailableCategoriesAsync(world.AgencyId, P(10, 20), SalesChannel.Web, Ct)).ShouldHaveSingleItem().Remaining.ShouldBe(2);
        (await ownQueries.GetCategoryAvailabilityAsync(category.Id, P(10, 20), SalesChannel.Web, cancellationToken: Ct))!.Remaining.ShouldBe(2);
        (await ownQueries.GetCapacityProfileAsync(category.Id, P(10, 20), cancellationToken: Ct)).ShouldNotBeNull();
    }

    [Fact]
    public async Task Null_arguments_are_bugs()
    {
        var world = new AvailabilityWorld();
        await using var services = world.Services();
        await using var scope = services.CreateAsyncScope();
        var queries = scope.ServiceProvider.GetRequiredService<IAvailabilityQueries>();

        await Should.ThrowAsync<ArgumentNullException>(() => queries.GetAvailableCategoriesAsync(world.AgencyId, null!, SalesChannel.Web, Ct));
        await Should.ThrowAsync<ArgumentNullException>(() => queries.GetCategoryAvailabilityAsync(Guid.CreateVersion7(), null!, SalesChannel.Web, cancellationToken: Ct));
        await Should.ThrowAsync<ArgumentNullException>(() => queries.GetCapacityProfileAsync(Guid.CreateVersion7(), null!, cancellationToken: Ct));
    }
}
