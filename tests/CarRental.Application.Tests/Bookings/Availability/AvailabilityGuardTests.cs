using CarRental.Application.Bookings.Availability;
using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Common.Concurrency;
using CarRental.Application.Fleet.Contracts;
using CarRental.Domain.Bookings.Availability;
using CarRental.Domain.Common;
using static CarRental.Application.Tests.Bookings.Availability.AvailabilityWorld;

namespace CarRental.Application.Tests.Bookings.Availability;

/// <summary>Write protocol (ADR-0025): lock the category, re-check on fresh data, then write in the same transaction.</summary>
public sealed class AvailabilityGuardTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static AvailabilityRequest Request(VehicleCategoryInfo category, Period rental, Guid? bookingId = null, SalesChannel channel = SalesChannel.Counter) =>
        new(category.Id, channel, Planned(bookingId ?? Guid.CreateVersion7(), rental));

    private static Task<Result> InUnitOfWork(AvailabilityWorld world, Func<CancellationToken, Task<Result>> operation) =>
        world.UnitOfWork.ExecuteAsync(operation, cancellationToken: Ct);

    [Fact]
    public async Task The_guard_only_runs_inside_a_unit_of_work()
    {
        var world = new AvailabilityWorld();
        var category = world.Category();
        var guard = world.Guard();

        await Should.ThrowAsync<InvalidOperationException>(() => guard.EnsureAvailableAsync(Request(category, P(10, 20)), cancellationToken: Ct));
        await Should.ThrowAsync<InvalidOperationException>(() => guard.LockAsync([category.Id], Ct));
        world.Journal.Entries.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_category_is_locked_before_any_data_is_read()
    {
        var world = new AvailabilityWorld();
        var category = world.Category();

        var result = await InUnitOfWork(world, ct => world.Guard().EnsureAvailableAsync(Request(category, P(10, 20)), cancellationToken: ct));

        result.IsSuccess.ShouldBeTrue();
        world.Lock.Acquired.ShouldBe([category.Id]);
        world.Journal.Entries[0].ShouldBe($"lock {category.Id}");
        world.Journal.Entries.ShouldContain("occupancy");
        world.Journal.Entries.ShouldContain("fleet.availability");
    }

    [Fact]
    public async Task The_last_vehicle_goes_to_the_first_writer()
    {
        var world = new AvailabilityWorld();
        var category = world.Category(vehicles: 1);
        world.Book(category, P(10, 20));

        var second = await InUnitOfWork(world, ct => world.Guard().EnsureAvailableAsync(Request(category, P(12, 18)), cancellationToken: ct));
        var later = await InUnitOfWork(world, ct => world.Guard().EnsureAvailableAsync(Request(category, P(22, 30)), cancellationToken: ct));

        second.Error.ShouldBe(AvailabilityErrors.NotAvailable);
        later.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task A_category_change_locks_both_categories_in_ascending_id_order()
    {
        var world = new AvailabilityWorld();
        var first = world.Category("ECMR");
        var second = world.Category("CDMR");
        var (lower, upper) = first.Id.CompareTo(second.Id) < 0 ? (first, second) : (second, first);

        await InUnitOfWork(world, ct => world.Guard().EnsureAvailableAsync(Request(upper, P(10, 20)), previousCategoryId: lower.Id, ct));
        await InUnitOfWork(world, ct => world.Guard().EnsureAvailableAsync(Request(lower, P(10, 20)), previousCategoryId: upper.Id, ct));

        world.Lock.Acquired.ShouldBe([lower.Id, upper.Id, lower.Id, upper.Id]);
    }

    [Fact]
    public async Task A_category_change_checks_the_new_category_not_the_old_one()
    {
        var world = new AvailabilityWorld();
        var economy = world.Category("ECMR", vehicles: 1);
        var compact = world.Category("CDMR", vehicles: 1);
        world.Book(economy, P(10, 20));
        var movingId = world.Book(economy, P(30, 40));
        var move = new AvailabilityRequest(compact.Id, SalesChannel.Phone, Planned(movingId, P(10, 20)));

        // The old category is full at those dates, the new one is free.
        (await InUnitOfWork(world, ct => world.Guard().EnsureAvailableAsync(move, economy.Id, ct))).IsSuccess.ShouldBeTrue();

        world.Book(compact, P(15, 16));
        (await InUnitOfWork(world, ct => world.Guard().EnsureAvailableAsync(move, economy.Id, ct))).Error.ShouldBe(AvailabilityErrors.NotAvailable);
    }

    [Fact]
    public async Task Two_concurrent_writers_for_the_last_vehicle_get_one_success_and_one_refusal()
    {
        for (var round = 0; round < 20; round++)
        {
            var world = new AvailabilityWorld();
            var category = world.Category(vehicles: 1);
            var locks = new TransactionalLocks();
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            async Task<Result> WriteAsync()
            {
                var unitOfWork = new TransactionalUnitOfWork();
                var guard = new AvailabilityGuard(unitOfWork, new ExclusiveAvailabilityLock(unitOfWork, locks), world.Loader(), world.Fleet);
                var booking = Planned(Guid.CreateVersion7(), P(10, 20));
                await start.Task;
                return await unitOfWork.ExecuteAsync(
                    async ct =>
                    {
                        var available = await guard.EnsureAvailableAsync(new AvailabilityRequest(category.Id, SalesChannel.Web, booking), cancellationToken: ct);
                        if (available.IsFailure)
                        {
                            return available;
                        }

                        // The write takes a while, inside the transaction that holds the lock.
                        await Task.Delay(10, ct);
                        world.Occupancy.Add(category.Id, booking);
                        return Result.Success();
                    },
                    cancellationToken: Ct);
            }

            var writers = new[] { Task.Run(WriteAsync, Ct), Task.Run(WriteAsync, Ct) };
            start.SetResult();
            var results = await Task.WhenAll(writers);

            results.Count(r => r.IsSuccess).ShouldBe(1);
            results.Single(r => r.IsFailure).Error.ShouldBe(AvailabilityErrors.NotAvailable);
            world.Occupancy.BookingsOf(category.Id).Count.ShouldBe(1);
        }
    }

    [Fact]
    public async Task A_booking_keeping_its_vehicle_needs_that_vehicle_free_even_when_the_category_has_room()
    {
        var world = new AvailabilityWorld();
        var category = world.Category(vehicles: 0);
        var pinned = world.Fleet.AddVehicle(category, "100 TU 0001", [Always]);
        var other = world.Fleet.AddVehicle(category, "100 TU 0002", [Always]);
        world.Book(category, P(10, 20), pinned.Id);
        var guard = world.Guard();

        // A second customer wants the same "guaranteed model" vehicle: the category still has a car, this one does not.
        var samePinned = new AvailabilityRequest(category.Id, SalesChannel.Web, Planned(Guid.CreateVersion7(), P(12, 18), pinned.Id));
        var otherPinned = samePinned with { Booking = Planned(Guid.CreateVersion7(), P(12, 18), other.Id) };
        var unpinned = samePinned with { Booking = Planned(Guid.CreateVersion7(), P(12, 18)) };

        (await InUnitOfWork(world, ct => guard.EnsureAvailableAsync(samePinned, cancellationToken: ct))).Error.ShouldBe(AvailabilityErrors.VehicleOverlap);
        (await InUnitOfWork(world, ct => guard.EnsureAvailableAsync(otherPinned, cancellationToken: ct))).IsSuccess.ShouldBeTrue();
        (await InUnitOfWork(world, ct => guard.EnsureAvailableAsync(unpinned, cancellationToken: ct))).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task Extending_a_rental_in_progress_keeps_its_vehicle_compliant_and_free()
    {
        // Vehicle 1 is insured until 30 h (Fleet cuts its window there); vehicle 2 gives the category room.
        var world = new AvailabilityWorld();
        var category = world.Category(vehicles: 0);
        var insuredUntil30 = world.Fleet.AddVehicle(category, "100 TU 0001", [P(-1000, 30)], compliantUntil: At(30));
        world.Fleet.AddVehicle(category, "100 TU 0002", [Always]);
        var rentalId = Guid.CreateVersion7();
        world.Occupancy.Add(category.Id, new BookingOccupancy(rentalId, OccupancyKind.InProgress, P(-5, 10), At(-5), assignedVehicleId: insuredUntil30.Id));
        var extension = new AvailabilityRequest(
            category.Id,
            SalesChannel.Counter,
            new BookingOccupancy(rentalId, OccupancyKind.InProgress, P(-5, 48), At(-5), assignedVehicleId: insuredUntil30.Id));
        var guard = world.Guard();

        (await InUnitOfWork(world, ct => guard.EnsureAvailableAsync(extension, cancellationToken: ct))).Error.ShouldBe(AvailabilityErrors.VehicleNotCompliant);
        (await InUnitOfWork(world, ct => guard.EnsureAvailableAsync(extension with { ComplianceWaived = true }, cancellationToken: ct))).IsSuccess.ShouldBeTrue();

        // The vehicle is promised to another customer at 25 h: no waiver allows an overlap.
        world.Book(category, P(25, 28), insuredUntil30.Id);
        (await InUnitOfWork(world, ct => guard.EnsureAvailableAsync(extension with { ComplianceWaived = true }, cancellationToken: ct)))
            .Error.ShouldBe(AvailabilityErrors.VehicleOverlap);
    }

    [Fact]
    public async Task A_category_change_must_release_or_replace_the_vehicle_of_the_old_category()
    {
        var world = new AvailabilityWorld();
        var economy = world.Category("ECMR", vehicles: 0);
        var compact = world.Category("CDMR", vehicles: 1);
        var economyCar = world.Fleet.AddVehicle(economy, "100 TU 0001", [Always]);
        var bookingId = world.Book(economy, P(10, 20), economyCar.Id);
        var keepingTheCar = new AvailabilityRequest(compact.Id, SalesChannel.Phone, Planned(bookingId, P(10, 20), economyCar.Id));
        var releasingTheCar = keepingTheCar with { Booking = Planned(bookingId, P(10, 20)) };

        (await InUnitOfWork(world, ct => world.Guard().EnsureAvailableAsync(keepingTheCar, economy.Id, ct))).Error.ShouldBe(AvailabilityErrors.VehicleNotInCategory);
        (await InUnitOfWork(world, ct => world.Guard().EnsureAvailableAsync(releasingTheCar, economy.Id, ct))).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task An_overdue_rental_cannot_move_to_a_vehicle_whose_documents_already_expired()
    {
        // Planned return 48 h ago and still out: the documents must be valid from now on, not only until the old return.
        var world = new AvailabilityWorld();
        var category = world.Category(vehicles: 0);
        var expired = world.Fleet.AddVehicle(category, "100 TU 0001", [P(-1000, -24)], compliantUntil: At(-24));
        var rental = new BookingOccupancy(Guid.CreateVersion7(), OccupancyKind.InProgress, P(-72, -48), At(-72));
        var swap = new VehicleAssignmentRequest(category.Id, rental, expired.Id);

        (await InUnitOfWork(world, ct => world.Guard().EnsureVehicleAssignableAsync(swap, ct))).Error.ShouldBe(AvailabilityErrors.VehicleNotCompliant);
        (await InUnitOfWork(world, ct => world.Guard().EnsureVehicleAssignableAsync(swap with { ComplianceWaived = true }, ct))).IsSuccess.ShouldBeTrue();

        // With a waiver nothing is left to check against the windows: a vehicle in the garage is still refused.
        var inTheGarage = world.Fleet.AddVehicle(category, "100 TU 0002", [], compliantUntil: At(-24), status: VehicleStatusInfo.Maintenance);
        (await InUnitOfWork(world, ct => world.Guard().EnsureVehicleAssignableAsync(new(category.Id, rental, inTheGarage.Id) { ComplianceWaived = true }, ct)))
            .Error.ShouldBe(AvailabilityErrors.VehicleUnavailable);
    }

    [Fact]
    public async Task A_category_is_locked_once_and_empty_ids_are_bugs()
    {
        var world = new AvailabilityWorld();
        var category = world.Category();

        await InUnitOfWork(world, ct => world.Guard().EnsureAvailableAsync(Request(category, P(10, 20)), previousCategoryId: category.Id, ct));
        await Should.ThrowAsync<ArgumentException>(() => InUnitOfWork(world, ct => world.Guard().LockAsync([category.Id, Guid.Empty], ct)));

        world.Lock.Acquired.ShouldBe([category.Id]);
    }

    [Fact]
    public async Task A_lock_failure_is_returned_before_anything_is_read()
    {
        var world = new AvailabilityWorld();
        var category = world.Category();
        world.Lock.Outcome = AvailabilityLockErrors.Timeout;

        var result = await InUnitOfWork(world, ct => world.Guard().EnsureAvailableAsync(Request(category, P(10, 20)), cancellationToken: ct));

        result.Error.ShouldBe(AvailabilityLockErrors.Timeout);
        world.Journal.Entries.ShouldBe([$"lock {category.Id}"]);
    }

    [Fact]
    public async Task An_amendment_leaves_the_amended_booking_out()
    {
        var world = new AvailabilityWorld();
        var category = world.Category(vehicles: 1);
        var bookingId = world.Book(category, P(10, 20));

        var result = await InUnitOfWork(world, ct => world.Guard().EnsureAvailableAsync(Request(category, P(10, 30), bookingId), cancellationToken: ct));

        result.IsSuccess.ShouldBeTrue();
        world.Occupancy.LastExcludedBookingId.ShouldBe(bookingId);
    }

    [Fact]
    public async Task Quotas_and_stop_sales_apply_to_the_requesting_channel_unless_an_agent_overrides_them()
    {
        var world = new AvailabilityWorld();
        var category = world.Category(vehicles: 2);
        world.Book(category, P(0, 100));
        world.Channels.Set(category.Id, SalesChannel.BookingCom, new ChannelRestrictionsInfo([], [P(0, 100)]));
        world.Channels.Set(category.Id, SalesChannel.Web, new ChannelRestrictionsInfo([new ReservedVehiclesInfo(P(0, 100), 1)], []));
        var guard = world.Guard();

        (await InUnitOfWork(world, ct => guard.EnsureAvailableAsync(Request(category, P(10, 20), channel: SalesChannel.BookingCom), cancellationToken: ct)))
            .Error.ShouldBe(AvailabilityErrors.NotAvailable);
        (await InUnitOfWork(world, ct => guard.EnsureAvailableAsync(Request(category, P(10, 20), channel: SalesChannel.Web), cancellationToken: ct)))
            .Error.ShouldBe(AvailabilityErrors.NotAvailable);
        (await InUnitOfWork(world, ct => guard.EnsureAvailableAsync(Request(category, P(10, 20), channel: SalesChannel.Counter), cancellationToken: ct)))
            .IsSuccess.ShouldBeTrue();

        var agentOverride = Request(category, P(10, 20), channel: SalesChannel.BookingCom) with { IgnoreChannelRestrictions = true };
        (await InUnitOfWork(world, ct => guard.EnsureAvailableAsync(agentOverride, cancellationToken: ct))).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task An_override_of_channel_restrictions_never_bypasses_the_physical_capacity()
    {
        var world = new AvailabilityWorld();
        var category = world.Category(vehicles: 1);
        world.Book(category, P(0, 100));
        var request = Request(category, P(10, 20), channel: SalesChannel.BookingCom) with { IgnoreChannelRestrictions = true };

        var result = await InUnitOfWork(world, ct => world.Guard().EnsureAvailableAsync(request, cancellationToken: ct));

        result.Error.ShouldBe(AvailabilityErrors.NotAvailable);
        world.Journal.Entries.ShouldNotContain(entry => entry.StartsWith("policy.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_preparation_buffer_of_the_agency_is_used_for_the_bookings_and_the_request()
    {
        var world = new AvailabilityWorld();
        var category = world.Category(vehicles: 1);
        world.Book(category, P(10, 20));
        world.Book(category, P(40, 50));

        // 2 h by default: [22 h, 38 h) fits between the two bookings.
        (await InUnitOfWork(world, ct => world.Guard().EnsureAvailableAsync(Request(category, P(22, 38)), cancellationToken: ct))).IsSuccess.ShouldBeTrue();

        world.Settings.BufferByAgency[world.AgencyId] = TimeSpan.FromHours(5);
        (await InUnitOfWork(world, ct => world.Guard().EnsureAvailableAsync(Request(category, P(24, 30)), cancellationToken: ct)))
            .Error.ShouldBe(AvailabilityErrors.NotAvailable);
        (await InUnitOfWork(world, ct => world.Guard().EnsureAvailableAsync(Request(category, P(25, 36)), cancellationToken: ct)))
            .Error.ShouldBe(AvailabilityErrors.NotAvailable);
        (await InUnitOfWork(world, ct => world.Guard().EnsureAvailableAsync(Request(category, P(25, 35)), cancellationToken: ct))).IsSuccess.ShouldBeTrue();
        world.Occupancy.LastBasis.ShouldBe(new OccupancyBasis(Now, TimeSpan.FromHours(5)));
    }

    [Fact]
    public async Task An_extension_of_a_rental_in_progress_only_counts_from_now()
    {
        // The rental started 30 h ago; in the past the category was over capacity (a vehicle sold since), which is settled.
        var world = new AvailabilityWorld();
        var category = world.Category(vehicles: 1);
        world.Book(category, P(-40, -20));
        var rentalId = Guid.CreateVersion7();
        world.Occupancy.Add(category.Id, new BookingOccupancy(rentalId, OccupancyKind.InProgress, P(-30, 5), At(-30)));
        var extension = new AvailabilityRequest(category.Id, SalesChannel.Counter, new BookingOccupancy(rentalId, OccupancyKind.InProgress, P(-30, 48), At(-30)));

        var result = await InUnitOfWork(world, ct => world.Guard().EnsureAvailableAsync(extension, cancellationToken: ct));

        result.IsSuccess.ShouldBeTrue();
        world.Occupancy.LastRange.ShouldBe(P(0, 50));
    }

    [Fact]
    public async Task An_overdue_rental_blocks_the_next_request_until_now_plus_buffer()
    {
        var world = new AvailabilityWorld();
        var category = world.Category(vehicles: 1);
        world.Occupancy.Add(category.Id, new BookingOccupancy(Guid.CreateVersion7(), OccupancyKind.InProgress, P(-48, -3), At(-48)));

        (await InUnitOfWork(world, ct => world.Guard().EnsureAvailableAsync(Request(category, P(1, 10)), cancellationToken: ct)))
            .Error.ShouldBe(AvailabilityErrors.NotAvailable);
        (await InUnitOfWork(world, ct => world.Guard().EnsureAvailableAsync(Request(category, P(2, 10)), cancellationToken: ct))).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task A_booking_that_occupies_nothing_from_now_on_needs_no_capacity()
    {
        var world = new AvailabilityWorld();
        var category = world.Category(vehicles: 0);
        var draft = new AvailabilityRequest(category.Id, SalesChannel.Web, new BookingOccupancy(Guid.CreateVersion7(), OccupancyKind.None, P(10, 20)));

        (await InUnitOfWork(world, ct => world.Guard().EnsureAvailableAsync(draft, cancellationToken: ct))).IsSuccess.ShouldBeTrue();
        (await InUnitOfWork(world, ct => world.Guard().EnsureAvailableAsync(Request(category, P(-30, -10)), cancellationToken: ct))).IsSuccess.ShouldBeTrue();
        (await InUnitOfWork(world, ct => world.Guard().EnsureAvailableAsync(Request(category, P(10, 20)), cancellationToken: ct)))
            .Error.ShouldBe(AvailabilityErrors.NotAvailable);
    }

    [Fact]
    public async Task An_unknown_category_is_reported()
    {
        var world = new AvailabilityWorld();
        var request = new AvailabilityRequest(Guid.CreateVersion7(), SalesChannel.Web, Planned(Guid.CreateVersion7(), P(10, 20)));

        var result = await InUnitOfWork(world, ct => world.Guard().EnsureAvailableAsync(request, cancellationToken: ct));

        result.Error.ShouldBe(AvailabilityErrors.CategoryNotFound);
    }

    [Fact]
    public async Task A_free_compliant_vehicle_can_be_assigned()
    {
        var world = new AvailabilityWorld();
        var category = world.Category(vehicles: 0);
        var vehicle = world.Fleet.AddVehicle(category, "123 TU 4567", [Always]);
        var bookingId = world.Book(category, P(10, 20));
        var request = new VehicleAssignmentRequest(category.Id, Planned(bookingId, P(10, 20)), vehicle.Id);

        var result = await InUnitOfWork(world, ct => world.Guard().EnsureVehicleAssignableAsync(request, ct));

        result.IsSuccess.ShouldBeTrue();
        world.Journal.Entries[0].ShouldBe($"lock {category.Id}");
        world.Occupancy.LastExcludedBookingId.ShouldBe(bookingId);
    }

    [Fact]
    public async Task A_vehicle_carrying_an_overlapping_booking_is_refused_buffer_included()
    {
        var world = new AvailabilityWorld();
        var category = world.Category(vehicles: 0);
        var vehicle = world.Fleet.AddVehicle(category, "123 TU 4567", [Always]);
        world.Book(category, P(0, 9), vehicle.Id);
        var request = new VehicleAssignmentRequest(category.Id, Planned(Guid.CreateVersion7(), P(10, 20)), vehicle.Id);

        var result = await InUnitOfWork(world, ct => world.Guard().EnsureVehicleAssignableAsync(request, ct));

        result.Error.ShouldBe(AvailabilityErrors.VehicleOverlap);
    }

    [Fact]
    public async Task Documents_expiring_before_the_return_block_the_assignment_unless_the_manager_waives_them()
    {
        var world = new AvailabilityWorld();
        var category = world.Category(vehicles: 0);
        var vehicle = world.Fleet.AddVehicle(category, "123 TU 4567", [P(-100, 15)], compliantUntil: At(15));
        var request = new VehicleAssignmentRequest(category.Id, Planned(Guid.CreateVersion7(), P(10, 20)), vehicle.Id);

        (await InUnitOfWork(world, ct => world.Guard().EnsureVehicleAssignableAsync(request, ct))).Error.ShouldBe(AvailabilityErrors.VehicleNotCompliant);
        (await InUnitOfWork(world, ct => world.Guard().EnsureVehicleAssignableAsync(request with { ComplianceWaived = true }, ct))).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task A_vehicle_in_maintenance_sold_or_of_another_category_cannot_be_assigned()
    {
        var world = new AvailabilityWorld();
        var category = world.Category(vehicles: 0);
        var other = world.Category("CDMR", vehicles: 0);
        var inTheGarage = world.Fleet.AddVehicle(category, "100 TU 0001", [P(-100, 12), P(14, 100)]);
        var sold = world.Fleet.AddVehicle(category, "100 TU 0002", [], status: VehicleStatusInfo.Sold);
        var elsewhere = world.Fleet.AddVehicle(other, "100 TU 0003", [Always]);
        var booking = Planned(Guid.CreateVersion7(), P(10, 20));
        var guard = world.Guard();

        (await InUnitOfWork(world, ct => guard.EnsureVehicleAssignableAsync(new(category.Id, booking, inTheGarage.Id), ct)))
            .Error.ShouldBe(AvailabilityErrors.VehicleUnavailable);
        (await InUnitOfWork(world, ct => guard.EnsureVehicleAssignableAsync(new(category.Id, booking, sold.Id) { ComplianceWaived = true }, ct)))
            .Error.ShouldBe(AvailabilityErrors.VehicleUnavailable);
        (await InUnitOfWork(world, ct => guard.EnsureVehicleAssignableAsync(new(category.Id, booking, elsewhere.Id), ct)))
            .Error.ShouldBe(AvailabilityErrors.VehicleNotInCategory);
        (await InUnitOfWork(world, ct => guard.EnsureVehicleAssignableAsync(new(category.Id, booking, Guid.CreateVersion7()), ct)))
            .Error.ShouldBe(AvailabilityErrors.VehicleNotInCategory);
        (await InUnitOfWork(world, ct => guard.EnsureVehicleAssignableAsync(new(Guid.CreateVersion7(), booking, elsewhere.Id), ct)))
            .Error.ShouldBe(AvailabilityErrors.CategoryNotFound);
    }

    [Fact]
    public async Task A_vehicle_swap_during_a_rental_only_considers_the_future()
    {
        // Vehicle 2 carried another booking until 2 h ago; the rental in progress moves to it now.
        var world = new AvailabilityWorld();
        var category = world.Category(vehicles: 0);
        var vehicle = world.Fleet.AddVehicle(category, "123 TU 4567", [Always]);
        world.Book(category, P(-20, -2), vehicle.Id);
        var rental = new BookingOccupancy(Guid.CreateVersion7(), OccupancyKind.InProgress, P(-30, 20), At(-30));

        var result = await InUnitOfWork(world, ct => world.Guard().EnsureVehicleAssignableAsync(new(category.Id, rental, vehicle.Id), ct));

        result.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task Assigning_a_vehicle_to_a_booking_entirely_in_the_past_has_nothing_to_protect()
    {
        // Correcting the history: the vehicle has been in the garage since, which does not matter any more.
        var world = new AvailabilityWorld();
        var category = world.Category(vehicles: 0);
        var vehicle = world.Fleet.AddVehicle(category, "123 TU 4567", [P(-1000, -100)]);
        var request = new VehicleAssignmentRequest(category.Id, Planned(Guid.CreateVersion7(), P(-50, -20)), vehicle.Id);

        var result = await InUnitOfWork(world, ct => world.Guard().EnsureVehicleAssignableAsync(request, ct));

        result.IsSuccess.ShouldBeTrue();
        world.Journal.Entries.ShouldNotContain("occupancy");
    }

    [Fact]
    public async Task Assigning_a_vehicle_requires_a_unit_of_work_and_reports_lock_failures()
    {
        var world = new AvailabilityWorld();
        var category = world.Category(vehicles: 1);
        var request = new VehicleAssignmentRequest(category.Id, Planned(Guid.CreateVersion7(), P(10, 20)), Guid.CreateVersion7());

        await Should.ThrowAsync<InvalidOperationException>(() => world.Guard().EnsureVehicleAssignableAsync(request, Ct));
        world.Lock.Outcome = AvailabilityLockErrors.Deadlock;
        (await InUnitOfWork(world, ct => world.Guard().EnsureVehicleAssignableAsync(request, ct))).Error.ShouldBe(AvailabilityLockErrors.Deadlock);
        await Should.ThrowAsync<ArgumentNullException>(() => world.Guard().EnsureVehicleAssignableAsync(null!, Ct));
        await Should.ThrowAsync<ArgumentNullException>(() => world.Guard().EnsureAvailableAsync(null!, cancellationToken: Ct));
    }

    [Fact]
    public async Task The_default_channel_policy_restricts_nothing()
    {
        var restrictions = await new NoChannelRestrictions().GetRestrictionsAsync(Guid.CreateVersion7(), SalesChannel.BookingCom, P(0, 10), Ct);

        restrictions.ShouldBeSameAs(ChannelRestrictionsInfo.None);
        restrictions.ReservedForOtherChannels.ShouldBeEmpty();
        restrictions.StopSales.ShouldBeEmpty();
    }
}
