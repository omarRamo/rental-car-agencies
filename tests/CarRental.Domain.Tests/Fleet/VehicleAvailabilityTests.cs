using CarRental.Domain.Common;
using CarRental.Domain.Fleet;
using static CarRental.Domain.Tests.Fleet.FleetTestData;

namespace CarRental.Domain.Tests.Fleet;

/// <summary>Rentable windows of a vehicle (docs/conception/phase-1-exploitation.md Â§3), evaluated at 20/10/2026 10:00 Tunis.</summary>
public sealed class VehicleAvailabilityTests
{
    private static readonly DateTimeOffset RangeStart = Tunis(2026, 11, 1);
    private static readonly DateTimeOffset RangeEnd = Tunis(2026, 12, 1);
    private static readonly Period November = P(RangeStart, RangeEnd);
    private static readonly DateTimeOffset FarFuture = Tunis(2028, 1, 1);

    private static VehicleAvailabilityProfile Profile(
        DateOnly? inServiceOn = null,
        VehicleStatus status = VehicleStatus.Active,
        DateTimeOffset? soldAt = null,
        DateTimeOffset? compliantUntil = null,
        bool compliant = true,
        params Period[] blocks) =>
        new(inServiceOn ?? new DateOnly(2024, 1, 1), status, soldAt, compliant ? compliantUntil ?? FarFuture : null, blocks);

    private static IReadOnlyList<Period> Windows(VehicleAvailabilityProfile profile, Period? range = null, DateTimeOffset? now = null) =>
        VehicleAvailability.Windows(profile, range ?? November, now ?? Now);

    [Fact]
    public void An_active_compliant_vehicle_is_available_over_the_whole_range()
    {
        Windows(Profile()).ShouldBe([November]);
    }

    [Fact]
    public void Windows_start_at_the_beginning_of_the_tunis_day_the_vehicle_is_put_in_service()
    {
        var windows = Windows(Profile(inServiceOn: new DateOnly(2026, 11, 10)));

        windows.ShouldBe([P(Tunis(2026, 11, 10), RangeEnd)]);
        windows[0].Start.ShouldBe(new DateTimeOffset(2026, 11, 9, 23, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void A_vehicle_put_in_service_after_the_range_has_no_window()
    {
        Windows(Profile(inServiceOn: new DateOnly(2026, 12, 1))).ShouldBeEmpty();
        Windows(Profile(inServiceOn: new DateOnly(2027, 1, 15))).ShouldBeEmpty();
    }

    [Fact]
    public void Windows_end_with_the_compliance_at_the_end_of_the_tunis_day_of_the_earliest_expiry()
    {
        var compliantUntil = VehicleCompliance.EndOfTunisDay(new DateOnly(2026, 11, 20));

        var windows = Windows(Profile(compliantUntil: compliantUntil));

        windows.ShouldBe([P(RangeStart, Tunis(2026, 11, 21))]);
    }

    [Fact]
    public void A_vehicle_whose_compliance_ended_before_the_range_has_no_window()
    {
        Windows(Profile(compliantUntil: RangeStart)).ShouldBeEmpty();
        Windows(Profile(compliantUntil: Tunis(2026, 10, 1))).ShouldBeEmpty();
    }

    [Fact]
    public void A_vehicle_missing_a_mandatory_document_has_no_window()
    {
        Windows(Profile(compliant: false)).ShouldBeEmpty();
    }

    [Fact]
    public void A_sold_vehicle_is_unavailable_after_the_sale()
    {
        var soldAt = Tunis(2026, 11, 15, 14);

        Windows(Profile(status: VehicleStatus.Sold, soldAt: soldAt)).ShouldBe([P(RangeStart, soldAt)]);
        Windows(Profile(status: VehicleStatus.Sold, soldAt: Tunis(2026, 10, 1))).ShouldBeEmpty();
    }

    [Fact]
    public void A_sold_vehicle_without_sale_date_has_no_window()
    {
        Windows(Profile(status: VehicleStatus.Sold)).ShouldBeEmpty();
    }

    [Fact]
    public void A_maintenance_block_splits_the_range_with_half_open_bounds()
    {
        var block = P(Tunis(2026, 11, 10, 8), Tunis(2026, 11, 12, 18));

        var windows = Windows(Profile(blocks: [block]));

        windows.ShouldBe([P(RangeStart, block.Start), P(block.End, RangeEnd)]);
        windows[0].Contains(block.Start).ShouldBeFalse();
        windows[1].Contains(block.End).ShouldBeTrue();
    }

    [Fact]
    public void Blocks_touching_the_range_bounds_shorten_the_window()
    {
        var atStart = P(Tunis(2026, 10, 30), Tunis(2026, 11, 3));
        var atEnd = P(Tunis(2026, 11, 28), Tunis(2026, 12, 5));

        Windows(Profile(blocks: [atStart, atEnd])).ShouldBe([P(atStart.End, atEnd.Start)]);
    }

    [Fact]
    public void A_block_covering_the_whole_range_leaves_no_window()
    {
        Windows(Profile(blocks: [P(Tunis(2026, 10, 25), Tunis(2026, 12, 10))])).ShouldBeEmpty();
        Windows(Profile(blocks: [November])).ShouldBeEmpty();
    }

    [Fact]
    public void Overlapping_nested_and_unordered_blocks_are_all_removed()
    {
        var late = P(Tunis(2026, 11, 20), Tunis(2026, 11, 22));
        var first = P(Tunis(2026, 11, 5), Tunis(2026, 11, 9));
        var overlapping = P(Tunis(2026, 11, 8), Tunis(2026, 11, 11));
        var nested = P(Tunis(2026, 11, 6), Tunis(2026, 11, 7));

        var windows = Windows(Profile(blocks: [late, overlapping, nested, first]));

        windows.ShouldBe([P(RangeStart, first.Start), P(overlapping.End, late.Start), P(late.End, RangeEnd)]);
    }

    [Fact]
    public void Back_to_back_blocks_leave_no_empty_window_between_them()
    {
        var first = P(Tunis(2026, 11, 5), Tunis(2026, 11, 6));
        var second = P(Tunis(2026, 11, 6), Tunis(2026, 11, 7));

        Windows(Profile(blocks: [first, second])).ShouldBe([P(RangeStart, first.Start), P(second.End, RangeEnd)]);
    }

    [Fact]
    public void Blocks_outside_the_range_change_nothing()
    {
        Windows(Profile(blocks: [P(Tunis(2026, 10, 1), Tunis(2026, 10, 5)), P(Tunis(2026, 12, 1), Tunis(2026, 12, 3))])).ShouldBe([November]);
    }

    [Theory]
    [InlineData(VehicleStatus.Maintenance)]
    [InlineData(VehicleStatus.OutOfService)]
    public void A_blocking_status_without_dated_block_makes_the_vehicle_unavailable_from_now_on(VehicleStatus status)
    {
        var now = Tunis(2026, 11, 14, 9, 30);

        Windows(Profile(status: status), now: now).ShouldBe([P(RangeStart, now)]);
        Windows(Profile(status: status), now: Now).ShouldBeEmpty();
    }

    [Fact]
    public void A_blocking_status_does_not_change_the_past()
    {
        var october = P(Tunis(2026, 10, 1), Tunis(2026, 10, 15));

        Windows(Profile(status: VehicleStatus.OutOfService), october, Now).ShouldBe([october]);
    }

    [Fact]
    public void A_dated_block_covering_now_replaces_the_open_ended_status_block()
    {
        var now = Tunis(2026, 11, 10, 12);
        var block = P(Tunis(2026, 11, 10, 8), Tunis(2026, 11, 12, 18));

        var windows = Windows(Profile(status: VehicleStatus.Maintenance, blocks: [block]), now: now);

        windows.ShouldBe([P(RangeStart, block.Start), P(block.End, RangeEnd)]);
    }

    [Fact]
    public void A_dated_block_that_does_not_cover_now_does_not_lift_the_status_block()
    {
        var now = Tunis(2026, 11, 10, 12);
        var later = P(Tunis(2026, 11, 20), Tunis(2026, 11, 21));
        var endedAtNow = P(Tunis(2026, 11, 9), now);

        Windows(Profile(status: VehicleStatus.Maintenance, blocks: [later]), now: now).ShouldBe([P(RangeStart, now)]);
        Windows(Profile(status: VehicleStatus.OutOfService, blocks: [endedAtNow]), now: now).ShouldBe([P(RangeStart, endedAtNow.Start)]);
    }

    [Fact]
    public void An_active_vehicle_ignores_the_current_instant()
    {
        var now = Tunis(2026, 11, 10, 12);

        Windows(Profile(), now: now).ShouldBe([November]);
    }

    [Fact]
    public void Every_limit_combines_with_the_others()
    {
        var block = P(Tunis(2026, 11, 3), Tunis(2026, 11, 4));

        var windows = Windows(Profile(
            inServiceOn: new DateOnly(2026, 11, 2),
            status: VehicleStatus.Sold,
            soldAt: Tunis(2026, 11, 25),
            compliantUntil: VehicleCompliance.EndOfTunisDay(new DateOnly(2026, 11, 20)),
            blocks: [block]));

        windows.ShouldBe([P(Tunis(2026, 11, 2), block.Start), P(block.End, Tunis(2026, 11, 21))]);
    }

    [Fact]
    public void Windows_are_clipped_to_a_short_range()
    {
        var range = P(Tunis(2026, 11, 10, 9), Tunis(2026, 11, 10, 11));

        Windows(Profile(), range).ShouldBe([range]);
        Windows(Profile(blocks: [P(Tunis(2026, 11, 10, 10), Tunis(2026, 11, 10, 10, 30))]), range)
            .ShouldBe([P(range.Start, Tunis(2026, 11, 10, 10)), P(Tunis(2026, 11, 10, 10, 30), range.End)]);
    }

    [Fact]
    public void Null_arguments_are_bugs()
    {
        Should.Throw<ArgumentNullException>(() => VehicleAvailability.Windows(null!, November, Now));
        Should.Throw<ArgumentNullException>(() => VehicleAvailability.Windows(Profile(), null!, Now));
    }

    [Fact]
    public void The_aggregate_computes_its_windows_from_its_documents_blocks_and_status()
    {
        var vehicle = NewVehicle();
        var block = P(Tunis(2026, 11, 10, 8), Tunis(2026, 11, 12, 18));
        vehicle.ScheduleMaintenance(Maintenance(block.Start, block.End), Stamp()).IsSuccess.ShouldBeTrue();

        vehicle.AvailabilityWindows(November, Now).ShouldBe([P(RangeStart, block.Start), P(block.End, RangeEnd)]);
        vehicle.CompliantUntil.ShouldBe(VehicleCompliance.EndOfTunisDay(new DateOnly(2027, 6, 30)));
        vehicle.GetAvailabilityProfile().MaintenanceBlocks.ShouldBe([block]);
    }
}
