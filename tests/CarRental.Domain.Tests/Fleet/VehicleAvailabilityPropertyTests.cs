using CarRental.Domain.Common;
using CarRental.Domain.Fleet;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;

namespace CarRental.Domain.Tests.Fleet;

/// <summary>
/// Properties of the rentable windows on thousands of random vehicles (in-service date, status, sale, compliance,
/// overlapping blocks, current instant) and ranges, checked against a point-wise oracle.
/// </summary>
public sealed class VehicleAvailabilityPropertyTests
{
    private static readonly DateOnly BaseDate = new(2026, 11, 1);
    private static readonly DateTimeOffset Origin = TunisTime.StartOfDay(BaseDate);

    [Property(MaxTest = 3000)]
    public Property Windows_are_ordered_disjoint_and_never_touching() =>
        Prop.ForAll(Scenarios(), s =>
        {
            var windows = s.Windows();
            return windows.Zip(windows.Skip(1)).All(pair => pair.First.End < pair.Second.Start);
        });

    [Property(MaxTest = 3000)]
    public Property Windows_lie_inside_the_range() =>
        Prop.ForAll(Scenarios(), s => s.Windows().All(w => s.Range.Contains(w)));

    [Property(MaxTest = 3000)]
    public Property Windows_never_overlap_a_maintenance_block() =>
        Prop.ForAll(Scenarios(), s => s.Windows().All(w => s.Profile.MaintenanceBlocks.All(b => !b.Overlaps(w))));

    [Property(MaxTest = 3000)]
    public Property Windows_stay_within_the_life_and_compliance_of_the_vehicle() =>
        Prop.ForAll(Scenarios(), s => s.Windows().All(w =>
            w.Start >= TunisTime.StartOfDay(s.Profile.InServiceOn)
            && s.Profile.CompliantUntil is { } until && w.End <= until
            && (s.Profile.SoldAtUtc is not { } soldAt || w.End <= soldAt)));

    [Property(MaxTest = 3000)]
    public Property A_blocking_status_without_a_block_covering_now_leaves_nothing_after_now() =>
        Prop.ForAll(Scenarios(), s =>
            s.Profile.Status is not (VehicleStatus.Maintenance or VehicleStatus.OutOfService)
            || s.Profile.MaintenanceBlocks.Any(b => b.Contains(s.Now))
            || s.Windows().All(w => w.End <= s.Now));

    [Property(MaxTest = 3000)]
    public Property An_instant_of_the_range_is_in_a_window_exactly_when_the_vehicle_is_rentable_then() =>
        Prop.ForAll(Scenarios(), s =>
        {
            var windows = s.Windows();
            return s.SampleInstants().All(t => windows.Any(w => w.Contains(t)) == s.IsRentable(t));
        });

    private static Arbitrary<Scenario> Scenarios()
    {
        var blocks =
            from start in Gen.Choose(-2_000, 9_000)
            from length in Gen.Choose(1, 3_000)
            select Period.Create(At(start), At(start + length)).Value;

        var scenarios =
            from rangeStart in Gen.Choose(0, 3_000)
            from rangeLength in Gen.Choose(1, 6_000)
            from inServiceDays in Gen.Choose(-5, 6)
            from status in Gen.Elements(VehicleStatus.Active, VehicleStatus.Maintenance, VehicleStatus.OutOfService, VehicleStatus.Sold)
            from sold in Gen.Elements(true, false)
            from soldAt in Gen.Choose(-1_000, 9_000)
            from compliant in Gen.Frequency((5, Gen.Constant(true)), (1, Gen.Constant(false)))
            from compliantUntil in Gen.Choose(-1_000, 10_000)
            from count in Gen.Choose(0, 6)
            from list in blocks.ListOf(count)
            from now in Gen.Choose(-2_000, 10_000)
            select new Scenario(
                new VehicleAvailabilityProfile(
                    BaseDate.AddDays(inServiceDays),
                    status,
                    sold || status == VehicleStatus.Sold && soldAt % 5 != 0 ? At(soldAt) : null,
                    compliant ? At(compliantUntil) : null,
                    [.. list]),
                Period.Create(At(rangeStart), At(rangeStart + rangeLength)).Value,
                At(now));

        return scenarios.ToArbitrary();
    }

    private static DateTimeOffset At(int minutes) => Origin.AddMinutes(minutes);

    public sealed record Scenario(VehicleAvailabilityProfile Profile, Period Range, DateTimeOffset Now)
    {
        public IReadOnlyList<Period> Windows() => VehicleAvailability.Windows(Profile, Range, Now);

        /// <summary>Oracle: the rules of §3 evaluated at one instant.</summary>
        public bool IsRentable(DateTimeOffset t)
        {
            if (Profile.CompliantUntil is not { } until || (Profile.Status == VehicleStatus.Sold && Profile.SoldAtUtc is null))
            {
                return false;
            }

            var statusBlocks = Profile.Status is VehicleStatus.Maintenance or VehicleStatus.OutOfService
                && !Profile.MaintenanceBlocks.Any(b => b.Contains(Now));
            return t >= TunisTime.StartOfDay(Profile.InServiceOn)
                && t < until
                && (Profile.SoldAtUtc is not { } soldAt || t < soldAt)
                && !Profile.MaintenanceBlocks.Any(b => b.Contains(t))
                && !(statusBlocks && t >= Now);
        }

        /// <summary>Every bound that can open or close a window, just before and at it, plus the middle of the range.</summary>
        public IEnumerable<DateTimeOffset> SampleInstants()
        {
            var bounds = new List<DateTimeOffset>
            {
                Range.Start,
                Range.End,
                Range.Start + (Range.Duration / 2),
                Now,
                TunisTime.StartOfDay(Profile.InServiceOn),
            };
            if (Profile.CompliantUntil is { } until)
            {
                bounds.Add(until);
            }

            if (Profile.SoldAtUtc is { } soldAt)
            {
                bounds.Add(soldAt);
            }

            foreach (var block in Profile.MaintenanceBlocks)
            {
                bounds.Add(block.Start);
                bounds.Add(block.End);
            }

            return bounds
                .SelectMany(b => new[] { b.AddTicks(-1), b })
                .Where(Range.Contains)
                .Distinct();
        }
    }
}
