using CarRental.Application.Fleet;
using CarRental.Domain.Common;
using CarRental.Domain.Fleet;

namespace CarRental.Application.Tests.Reporting;

/// <summary>
/// The rentable windows of a vehicle judged on its history (the Fleet contract of the reports, ADR-0045): past status
/// periods, documents valid on each day (renewals included), maintenance blocks and category changes.
/// </summary>
public sealed class FleetHistoryTests
{
    private static readonly DateOnly First = new(2026, 7, 1);
    private static readonly Period July = Period.Create(TunisTime.StartOfDay(First), TunisTime.StartOfDay(First.AddDays(31))).Value;
    private static readonly Guid Ecmr = Guid.CreateVersion7();
    private static readonly Guid Cdmr = Guid.CreateVersion7();

    [Fact]
    public void A_past_out_of_service_period_stays_blocked_after_the_vehicle_came_back()
    {
        var profile = Profile(
            VehicleStatus.Active,
            statusChanges:
            [
                new(Day(5, 14), VehicleStatus.OutOfService),
                new(Day(9, 8), VehicleStatus.Active),
            ]);

        var windows = FleetHistory.Windows(profile, July);

        windows.Select(w => (w.Start, w.End)).ShouldBe([(July.Start, Day(5, 14)), (Day(9, 8), July.End)]);
    }

    [Fact]
    public void The_current_blocking_status_blocks_from_its_start_without_end()
    {
        var since = Day(20, 10);
        var profile = Profile(VehicleStatus.Maintenance, currentStatusSince: since, statusChanges: [new(since, VehicleStatus.Maintenance)]);

        FleetHistory.Windows(profile, July).ShouldHaveSingleItem().End.ShouldBe(since);
    }

    [Fact]
    public void A_history_without_its_lines_yields_to_the_current_status_of_the_vehicle()
    {
        var since = Day(12, 9);

        var blocked = FleetHistory.StatusBlocks(Profile(VehicleStatus.OutOfService, currentStatusSince: since)).ShouldHaveSingleItem();
        var closed = FleetHistory.StatusBlocks(Profile(VehicleStatus.Active, currentStatusSince: since, statusChanges: [new(Day(3, 9), VehicleStatus.Maintenance)]))
            .ShouldHaveSingleItem();

        blocked.Start.ShouldBe(since);
        blocked.End.ShouldBe(DateTimeOffset.MaxValue);
        (closed.Start, closed.End).ShouldBe((Day(3, 9), since));
    }

    [Fact]
    public void A_document_renewed_a_few_days_late_leaves_the_vehicle_non_compliant_in_between()
    {
        var profile = Profile(
            VehicleStatus.Active,
            documents:
            [
                new(VehicleDocumentType.Insurance, null, new DateOnly(2026, 7, 10)),
                new(VehicleDocumentType.Insurance, new DateOnly(2026, 7, 14), new DateOnly(2027, 7, 13)),
                new(VehicleDocumentType.RoadTax, null, new DateOnly(2027, 1, 31)),
                new(VehicleDocumentType.TechnicalInspection, null, new DateOnly(2027, 3, 31)),
                new(VehicleDocumentType.RegistrationCard, null, null),
            ]);

        var windows = FleetHistory.Windows(profile, July);

        windows.Select(w => (w.Start, w.End)).ShouldBe([(July.Start, StartOf(11)), (StartOf(14), July.End)]);
    }

    [Fact]
    public void A_missing_mandatory_document_makes_the_whole_range_non_compliant()
    {
        var profile = Profile(VehicleStatus.Active, documents: [new(VehicleDocumentType.Insurance, null, new DateOnly(2027, 1, 1))]);

        FleetHistory.Windows(profile, July).ShouldBeEmpty();
        FleetHistory.ComplianceGaps(profile.Documents, July).ShouldContain(July);
    }

    [Fact]
    public void Maintenance_blocks_the_sale_and_the_service_date_bound_the_windows()
    {
        var profile = Profile(
            VehicleStatus.Sold,
            inServiceOn: new DateOnly(2026, 7, 3),
            soldAt: Day(25, 12),
            blocks: [Period.Create(Day(10, 8), Day(11, 18)).Value]);

        var windows = FleetHistory.Windows(profile, July);

        windows.Select(w => (w.Start, w.End)).ShouldBe([(StartOf(3), Day(10, 8)), (Day(11, 18), Day(25, 12))]);
    }

    [Fact]
    public void The_category_of_a_day_follows_the_category_changes()
    {
        var profile = Profile(VehicleStatus.Active, currentCategory: Cdmr, categoryChanges: [new(Day(15, 10), Ecmr, Cdmr)]);

        FleetHistory.CategoryAt(profile, Day(15, 9)).ShouldBe(Ecmr);
        FleetHistory.CategoryAt(profile, Day(15, 10)).ShouldBe(Cdmr);
        FleetHistory.CategoryAt(Profile(VehicleStatus.Active, currentCategory: Cdmr), Day(1, 0)).ShouldBe(Cdmr);
    }

    private static DateTimeOffset Day(int day, int hour) => TunisTime.FromLocal(new DateOnly(2026, 7, day), new TimeOnly(hour, 0));

    private static DateTimeOffset StartOf(int day) => TunisTime.StartOfDay(new DateOnly(2026, 7, day));

    private static VehicleHistoryProfile Profile(
        VehicleStatus status,
        DateTimeOffset? currentStatusSince = null,
        IReadOnlyList<VehicleStatusChange>? statusChanges = null,
        IReadOnlyList<DocumentValidity>? documents = null,
        IReadOnlyList<Period>? blocks = null,
        DateOnly? inServiceOn = null,
        DateTimeOffset? soldAt = null,
        Guid? currentCategory = null,
        IReadOnlyList<VehicleCategoryChange>? categoryChanges = null) =>
        new(
            inServiceOn ?? new DateOnly(2025, 1, 1),
            soldAt,
            status,
            currentStatusSince ?? new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero),
            statusChanges ?? [],
            documents ?? AlwaysValid,
            blocks ?? [],
            currentCategory ?? Ecmr,
            categoryChanges ?? []);

    private static IReadOnlyList<DocumentValidity> AlwaysValid { get; } =
    [
        new(VehicleDocumentType.Insurance, null, new DateOnly(2030, 12, 31)),
        new(VehicleDocumentType.RoadTax, null, new DateOnly(2030, 12, 31)),
        new(VehicleDocumentType.TechnicalInspection, null, new DateOnly(2030, 12, 31)),
    ];
}
