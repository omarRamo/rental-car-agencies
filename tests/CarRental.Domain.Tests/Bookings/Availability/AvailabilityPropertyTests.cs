using System.Globalization;
using CarRental.Domain.Bookings.Availability;
using CarRental.Domain.Common;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using static CarRental.Domain.Tests.Bookings.Availability.AvailabilityTestData;

namespace CarRental.Domain.Tests.Bookings.Availability;

/// <summary>
/// Properties of the availability engine on thousands of random fleets and request sequences (PROMPT §8): invariant 2
/// (never more active bookings than vehicles in service, at any instant), invariant 1 (no overlapping assignment,
/// buffer included) and agreement with a brute-force reference. Every bound lies on a one-hour grid, so the reference
/// can simply evaluate every hour.
/// </summary>
public sealed class AvailabilityPropertyTests
{
    /// <summary>Requests start within [-6 h, Horizon h].</summary>
    private const int Horizon = 240;

    /// <summary>Hours scanned by the reference: every bound of the scenarios lies inside.</summary>
    private const int GridStart = -200;
    private const int GridEnd = Horizon + 250;

    private const int ChannelCount = 3;

    [Property(MaxTest = 2000)]
    public Property Accepted_requests_never_exceed_the_vehicles_in_service_at_any_instant() =>
        Prop.ForAll(Sequences(Fleets()).ToArbitrary(), scenario =>
        {
            var accepted = Replay(scenario);
            return Hours().All(hour => accepted.Values.Count(occupancy => occupancy.Contains(hour)) <= scenario.Fleet.CapacityAt(hour));
        });

    [Property(MaxTest = 1000)]
    public Property Identical_vehicles_always_in_service_carry_every_accepted_booking_without_overlap() =>
        Prop.ForAll(Sequences(IdenticalFleets(), restricted: false).ToArbitrary(), scenario =>
        {
            var accepted = Replay(scenario);
            var occupancyOf = accepted.ToDictionary(booking => BookingId(booking.Key), booking => booking.Value);
            var plan = GreedyVehicleAssigner.Assign(
                scenario.Fleet.ToVehicles(),
                [.. accepted.Select(booking => Booking(booking.Key, booking.Value.ToPeriod()))]);

            return plan.IsComplete
                && plan.Proposals.Count == accepted.Count
                && plan.Proposals.Select(p => p.BookingId).Distinct().Count() == accepted.Count
                && plan.Proposals.GroupBy(p => p.VehicleId).All(onVehicle => !AnyOverlap([.. onVehicle.Select(p => occupancyOf[p.BookingId])]));
        });

    [Property(MaxTest = 2000)]
    public Property Re_checking_an_accepted_booking_without_itself_always_succeeds() =>
        Prop.ForAll(Sequences(Fleets()).ToArbitrary(), scenario =>
        {
            var accepted = Replay(scenario);
            var calculator = new AvailabilityCalculator(
                scenario.Fleet.ToVehicles(),
                [.. accepted.Select(booking => Booking(booking.Key, booking.Value.ToPeriod()))]);

            return accepted.All(booking => calculator.CheckOccupancy(booking.Value.ToPeriod(), BookingId(booking.Key)).IsAvailable);
        });

    [Property(MaxTest = 3000)]
    public Property The_calculator_agrees_with_a_brute_force_reference() =>
        Prop.ForAll(Checks().ToArbitrary(), scenario =>
        {
            var calculator = new AvailabilityCalculator(
                scenario.Fleet.ToVehicles(),
                scenario.Intervals.Select(i => Booking(i.Booking, i.Span.ToPeriod())),
                scenario.Restrictions.ToDomain());
            var excluded = scenario.Excluded is { } number ? BookingId(number) : (Guid?)null;

            var actual = calculator.CheckOccupancy(scenario.Window.ToPeriod(), excluded);
            var expected = Reference(scenario);

            return actual.IsAvailable == expected.IsAvailable
                && actual.MinimumRemaining == expected.MinimumRemaining
                && actual.FirstConflict == expected.FirstConflict?.ToPeriod()
                && actual.ConflictReason == expected.Reason;
        });

    [Property(MaxTest = 2000)]
    public Property The_profile_agrees_with_a_brute_force_reference() =>
        Prop.ForAll(Checks().ToArbitrary(), scenario =>
        {
            var calculator = new AvailabilityCalculator(
                scenario.Fleet.ToVehicles(),
                scenario.Intervals.Select(i => Booking(i.Booking, i.Span.ToPeriod())),
                scenario.Restrictions.ToDomain());
            var excluded = scenario.Excluded is { } number ? BookingId(number) : (Guid?)null;

            var profile = calculator.Profile(scenario.Window.ToPeriod(), excluded);

            var contiguous = profile[0].Period.Start == scenario.Window.ToPeriod().Start
                && profile[^1].Period.End == scenario.Window.ToPeriod().End
                && profile.Zip(profile.Skip(1)).All(pair => pair.First.Period.End == pair.Second.Period.Start
                    && (pair.First.Capacity, pair.First.Occupancy) != (pair.Second.Capacity, pair.Second.Occupancy));
            var exact = profile.All(segment => HoursOf(segment.Period).All(hour =>
                segment.Capacity == scenario.CapacityAt(hour) && segment.Occupancy == scenario.OccupancyAt(hour)));
            return contiguous && exact;
        });

    [Property(MaxTest = 2000)]
    public Property Greedy_proposals_stay_inside_windows_and_never_overlap() =>
        Prop.ForAll(Assignments().ToArbitrary(), scenario =>
        {
            var plan = GreedyVehicleAssigner.Assign(scenario.Fleet.ToVehicles(), scenario.ToIntervals());
            var unassigned = scenario.Bookings.Where(b => b.Vehicle is null).Select(b => BookingId(b.Number)).ToHashSet();
            var spanOf = scenario.Bookings.ToDictionary(b => BookingId(b.Number), b => b.Span);
            var vehicleIndex = scenario.Fleet.Windows.Select((_, index) => (Id: VehicleId(index + 1), Index: index)).ToDictionary(v => v.Id, v => v.Index);

            // Final schedule of each vehicle: fixed bookings on known vehicles, then proposals.
            var schedule = scenario.Bookings
                .Where(b => b.Vehicle is { } v && v < scenario.Fleet.Windows.Count)
                .Select(b => (Vehicle: VehicleId(b.Vehicle!.Value + 1), b.Span))
                .Concat(plan.Proposals.Select(p => (Vehicle: p.VehicleId, Span: spanOf[p.BookingId])))
                .ToList();

            var everyUnassignedHandledOnce = plan.Proposals.Select(p => p.BookingId)
                .Concat(plan.Unassignable.Select(u => u.BookingId))
                .Order()
                .SequenceEqual(unassigned.Order());
            var proposalsInsideWindows = plan.Proposals.All(p => scenario.Fleet.Covers(vehicleIndex[p.VehicleId], spanOf[p.BookingId]));
            var proposalsWithoutOverlap = plan.Proposals.All(p =>
                schedule.Count(s => s.Vehicle == p.VehicleId && s.Span.Overlaps(spanOf[p.BookingId])) == 1);
            var truthfulReasons = plan.Unassignable.All(u =>
            {
                var span = spanOf[u.BookingId];
                var covering = Enumerable.Range(0, scenario.Fleet.Windows.Count).Where(index => scenario.Fleet.Covers(index, span)).ToList();
                return u.Reason == AvailabilityErrors.NoVehicleInService
                    ? covering.Count == 0
                    : u.Reason == AvailabilityErrors.AllVehiclesOccupied
                      && covering.Count > 0
                      && covering.All(index => schedule.Any(s => s.Vehicle == VehicleId(index + 1) && s.Span.Overlaps(span)));
            });
            var conflictsReported = scenario.Bookings.Where(b => b.Vehicle is not null).All(fixedBooking =>
            {
                var reported = plan.Conflicts.Where(c => c.BookingId == BookingId(fixedBooking.Number)).Select(c => c.Reason).ToList();
                if (fixedBooking.Vehicle >= scenario.Fleet.Windows.Count)
                {
                    return reported.SequenceEqual([AvailabilityErrors.VehicleNotInCategory]);
                }

                var overlapsAnother = scenario.Bookings.Any(other => other.Number != fixedBooking.Number
                    && other.Vehicle == fixedBooking.Vehicle
                    && other.Span.Overlaps(fixedBooking.Span));
                var rentable = scenario.Fleet.Covers(fixedBooking.Vehicle!.Value, fixedBooking.Span);
                return overlapsAnother
                    ? reported.Count <= 1 && reported.All(r => r == AvailabilityErrors.VehicleOverlap || r == AvailabilityErrors.VehicleUnavailable)
                    : rentable ? reported.Count == 0 : reported.SequenceEqual([AvailabilityErrors.VehicleUnavailable]);
            });
            var overlappingPairsReported = scenario.Bookings.Where(b => b.Vehicle is { } v && v < scenario.Fleet.Windows.Count)
                .SelectMany(a => scenario.Bookings.Where(b => b.Number > a.Number && b.Vehicle == a.Vehicle && b.Span.Overlaps(a.Span)), (a, b) => (a, b))
                .All(pair => plan.Conflicts.Any(c => c.BookingId == BookingId(pair.a.Number) || c.BookingId == BookingId(pair.b.Number)));

            return everyUnassignedHandledOnce && proposalsInsideWindows && proposalsWithoutOverlap && truthfulReasons
                && conflictsReported && overlappingPairsReported;
        });

    [Property(MaxTest = 2000)]
    public Property A_step_function_counts_its_weighted_periods_at_every_instant() =>
        Prop.ForAll(Gen.ArrayOf(Gen.Zip(Span(GridStart, GridEnd - 1, 1, 60), Gen.Choose(-3, 3))).ToArbitrary(), terms =>
        {
            var function = StepFunction.Sum(terms.Select(term => (Period: term.Item1.ToPeriod(), Weight: term.Item2)));
            var breakpoints = function.Breakpoints;

            var exact = Hours().All(hour => function.ValueAt(At(hour)) == terms.Where(t => t.Item1.Contains(hour)).Sum(t => t.Item2));
            var canonical = breakpoints.Zip(breakpoints.Skip(1)).All(pair => pair.First.At < pair.Second.At && pair.First.Value != pair.Second.Value)
                && (breakpoints.Count == 0 || breakpoints[0].Value != 0);
            return exact && canonical;
        });

    [Property(MaxTest = 2000)]
    public Property An_in_progress_rental_ends_at_its_return_or_now_whichever_is_later_plus_buffer() =>
        Prop.ForAll(
            Span(0, 100, 1, 100).ToArbitrary(),
            Gen.Choose(0, 300).ToArbitrary(),
            Gen.Choose(0, 4).ToArbitrary(),
            (rental, now, buffer) =>
            {
                var pickup = Math.Min(rental.From, now);
                var basis = new OccupancyBasis(At(now), TimeSpan.FromHours(buffer));
                var period = basis.PeriodOf(new BookingOccupancy(BookingId(1), OccupancyKind.InProgress, rental.ToPeriod(), At(pickup)))!;

                // End: max(return, now) + buffer, still including now when the buffer is zero.
                var end = At(Math.Max(rental.To, now) + buffer);
                var expectedEnd = end > At(now) ? end : At(now).AddTicks(1);
                return period.Start == At(pickup)
                    && period.Contains(At(now))
                    && period.End == expectedEnd;
            });

    // ---------------------------------------------------------------- replay and reference

    /// <summary>Plays the sequence: a request is accepted (or an amendment applied) only when the calculator says so.</summary>
    private static SortedDictionary<int, HourSpan> Replay(SequenceScenario scenario)
    {
        var vehicles = scenario.Fleet.ToVehicles();
        var restrictions = scenario.Channels.Select(channel => channel.ToDomain()).ToArray();
        var buffer = TimeSpan.FromHours(scenario.Buffer);
        var bookings = new SortedDictionary<int, HourSpan>();
        var nextNumber = 1;

        foreach (var operation in scenario.Operations)
        {
            var calculator = new AvailabilityCalculator(
                vehicles,
                [.. bookings.Select(booking => Booking(booking.Key, booking.Value.ToPeriod()))],
                restrictions[operation.Channel]);
            var occupancy = new HourSpan(operation.Rental.From, operation.Rental.To + scenario.Buffer);

            switch (operation.Kind)
            {
                case OperationKind.Book when calculator.Check(operation.Rental.ToPeriod(), buffer).IsAvailable:
                    bookings[nextNumber++] = occupancy;
                    break;
                case OperationKind.Amend when bookings.Count > 0:
                    var amended = bookings.Keys.ElementAt(operation.Target % bookings.Count);
                    if (calculator.Check(operation.Rental.ToPeriod(), buffer, BookingId(amended)).IsAvailable)
                    {
                        bookings[amended] = occupancy;
                    }

                    break;
                case OperationKind.Cancel when bookings.Count > 0:
                    bookings.Remove(bookings.Keys.ElementAt(operation.Target % bookings.Count));
                    break;
            }
        }

        return bookings;
    }

    private static ReferenceCheck Reference(CheckScenario scenario)
    {
        var remaining = HoursOf(scenario.Window).Select(hour => (Hour: hour, Remaining: scenario.CapacityAt(hour) - scenario.OccupancyAt(hour))).ToList();
        var minimum = remaining.Min(r => r.Remaining);
        var firstBad = remaining.FindIndex(r => r.Remaining < 1);
        if (firstBad < 0)
        {
            return new ReferenceCheck(true, minimum, null, AvailabilityConflictReason.None);
        }

        var lastBad = firstBad;
        while (lastBad + 1 < remaining.Count && remaining[lastBad + 1].Remaining < 1)
        {
            lastBad++;
        }

        var start = remaining[firstBad].Hour;
        var reason = scenario.Fleet.CapacityAt(start) - scenario.OccupancyAt(start) < 1
            ? AvailabilityConflictReason.Capacity
            : scenario.Restrictions.StopSales.Any(stop => stop.Contains(start))
                ? AvailabilityConflictReason.StopSale
                : AvailabilityConflictReason.Quota;
        return new ReferenceCheck(false, minimum, new HourSpan(start, remaining[lastBad].Hour + 1), reason);
    }

    private static bool AnyOverlap(IReadOnlyList<HourSpan> spans) =>
        spans.SelectMany((a, i) => spans.Skip(i + 1), (a, b) => a.Overlaps(b)).Any(overlap => overlap);

    private static IEnumerable<int> Hours() => Enumerable.Range(GridStart, GridEnd - GridStart);

    private static IEnumerable<int> HoursOf(HourSpan span) => Enumerable.Range(span.From, span.To - span.From);

    private static IEnumerable<int> HoursOf(Period period) =>
        Enumerable.Range((int)(period.Start - T0).TotalHours, (int)period.Duration.TotalHours);

    // ---------------------------------------------------------------- generators

    private static Gen<HourSpan> Span(int minStart, int maxStart, int minLength, int maxLength) =>
        from start in Gen.Choose(minStart, maxStart)
        from length in Gen.Choose(minLength, maxLength)
        select new HourSpan(start, start + length);

    /// <summary>0 to 5 vehicles; each is always rentable or has 0 to 3 random (possibly overlapping) windows.</summary>
    private static Gen<FleetSpec> Fleets() =>
        from count in Gen.Choose(0, 5)
        from windows in Gen.ArrayOf(VehicleWindows(), count)
        select new FleetSpec(windows);

    /// <summary>1 to 4 identical vehicles, always rentable.</summary>
    private static Gen<FleetSpec> IdenticalFleets() =>
        from count in Gen.Choose(1, 4)
        select new FleetSpec([.. Enumerable.Repeat<IReadOnlyList<HourSpan>>([new HourSpan(GridStart, GridEnd)], count)]);

    private static Gen<IReadOnlyList<HourSpan>> VehicleWindows() =>
        Gen.Frequency(
            (3, Gen.Constant<IReadOnlyList<HourSpan>>([new HourSpan(GridStart, GridEnd)])),
            (2, from count in Gen.Choose(0, 3)
                from spans in Gen.ArrayOf(Span(-60, Horizon + 60, 1, 150), count)
                select (IReadOnlyList<HourSpan>)spans));

    private static Gen<RestrictionSpec> Restrictions() =>
        from reservedCount in Gen.Choose(0, 3)
        from reserved in Gen.ArrayOf(Gen.Zip(Span(-20, Horizon + 20, 1, 120), Gen.Choose(0, 3)), reservedCount)
        from stopCount in Gen.Choose(0, 2)
        from stops in Gen.ArrayOf(Span(-20, Horizon + 20, 1, 48), stopCount)
        select new RestrictionSpec([.. reserved.Select(r => new ReservedSpec(r.Item1, r.Item2))], stops);

    private static Gen<Operation> Operations() =>
        from kind in Gen.Frequency(
            (6, Gen.Constant(OperationKind.Book)),
            (2, Gen.Constant(OperationKind.Amend)),
            (1, Gen.Constant(OperationKind.Cancel)))
        from target in Gen.Choose(0, 1000)
        from rental in Span(-6, Horizon, 1, 96)
        from channel in Gen.Choose(0, ChannelCount - 1)
        select new Operation(kind, target, rental, channel);

    private static Gen<SequenceScenario> Sequences(Gen<FleetSpec> fleets, bool restricted = true) =>
        from fleet in fleets
        from channels in restricted ? Gen.ArrayOf(Restrictions(), ChannelCount - 1) : Gen.Constant(Array.Empty<RestrictionSpec>())
        from buffer in Gen.Choose(0, 4)
        from count in Gen.Choose(0, 80)
        from operations in Gen.ArrayOf(Operations(), count)
        select new SequenceScenario(
            fleet,
            [RestrictionSpec.None, .. restricted ? channels : Enumerable.Repeat(RestrictionSpec.None, ChannelCount - 1)],
            buffer,
            operations);

    private static Gen<CheckScenario> Checks() =>
        from fleet in Fleets()
        from intervalCount in Gen.Choose(0, 15)
        from intervals in Gen.ArrayOf(Gen.Zip(Gen.Choose(1, 10), Span(-20, Horizon, 1, 100)), intervalCount)
        from restrictions in Gen.Frequency((1, Gen.Constant(RestrictionSpec.None)), (2, Restrictions()))
        from window in Span(-30, Horizon + 30, 1, 120)
        from excluded in Gen.Frequency((1, Gen.Constant<int?>(null)), (2, Gen.Choose(1, 10).Select(n => (int?)n)))
        select new CheckScenario(fleet, [.. intervals.Select(i => new IntervalSpec(i.Item1, i.Item2))], restrictions, window, excluded);

    private static Gen<AssignmentScenario> Assignments() =>
        from fleet in Gen.Choose(1, 5).SelectMany(count => Gen.ArrayOf(VehicleWindows(), count)).Select(w => new FleetSpec(w))
        from count in Gen.Choose(0, 30)
        from bookings in Gen.ArrayOf(
            Gen.Zip(
                Span(-20, Horizon, 1, 72),
                Gen.Frequency((2, Gen.Constant<int?>(null)), (1, Gen.Choose(0, 5).Select(v => (int?)v)))),
            count)
        select new AssignmentScenario(fleet, [.. bookings.Select((b, index) => new BookingSpec(index + 1, b.Item1, b.Item2))]);

    // ---------------------------------------------------------------- scenario data (hours from T0)

    private enum OperationKind
    {
        Book,
        Amend,
        Cancel,
    }

    private sealed record HourSpan(int From, int To)
    {
        public Period ToPeriod() => P(From, To);

        public bool Contains(int hour) => From <= hour && hour < To;

        public bool Overlaps(HourSpan other) => From < other.To && other.From < To;

        public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"[{From},{To})");
    }

    private sealed record FleetSpec(IReadOnlyList<IReadOnlyList<HourSpan>> Windows)
    {
        public IReadOnlyList<RentableVehicle> ToVehicles() =>
            [.. Windows.Select((windows, index) => Vehicle(index + 1, [.. windows.Select(w => w.ToPeriod())]))];

        public int CapacityAt(int hour) => Windows.Count(windows => windows.Any(w => w.Contains(hour)));

        /// <summary>Whether the union of the windows of vehicle <paramref name="index"/> covers every hour of the span.</summary>
        public bool Covers(int index, HourSpan span) =>
            Enumerable.Range(span.From, span.To - span.From).All(hour => Windows[index].Any(w => w.Contains(hour)));

        public override string ToString() =>
            string.Join("; ", Windows.Select((windows, index) => $"v{index + 1}: {string.Join(' ', windows)}"));
    }

    private sealed record ReservedSpec(HourSpan Span, int Vehicles)
    {
        public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Span}x{Vehicles}");
    }

    private sealed record RestrictionSpec(IReadOnlyList<ReservedSpec> Reserved, IReadOnlyList<HourSpan> StopSales)
    {
        public static RestrictionSpec None { get; } = new([], []);

        public ChannelRestrictions ToDomain() => new(
            Reserved.Select(r => new ReservedCapacity(r.Span.ToPeriod(), r.Vehicles)),
            StopSales.Select(s => s.ToPeriod()));

        public int CapacityAt(int hour, int fleet) =>
            StopSales.Any(stop => stop.Contains(hour)) ? 0 : Math.Max(0, fleet - Reserved.Where(r => r.Span.Contains(hour)).Sum(r => r.Vehicles));

        public override string ToString() => $"reserved: {string.Join(' ', Reserved)} stops: {string.Join(' ', StopSales)}";
    }

    private sealed record Operation(OperationKind Kind, int Target, HourSpan Rental, int Channel)
    {
        public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Kind}#{Target}{Rental}@{Channel}");
    }

    private sealed record SequenceScenario(FleetSpec Fleet, IReadOnlyList<RestrictionSpec> Channels, int Buffer, IReadOnlyList<Operation> Operations)
    {
        public override string ToString() =>
            $"fleet {{{Fleet}}} channels {{{string.Join(" | ", Channels)}}} buffer {Buffer} h ops {string.Join(' ', Operations)}";
    }

    private sealed record IntervalSpec(int Booking, HourSpan Span)
    {
        public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"b{Booking}{Span}");
    }

    private sealed record CheckScenario(FleetSpec Fleet, IReadOnlyList<IntervalSpec> Intervals, RestrictionSpec Restrictions, HourSpan Window, int? Excluded)
    {
        public int CapacityAt(int hour) => Restrictions.CapacityAt(hour, Fleet.CapacityAt(hour));

        public int OccupancyAt(int hour) => Intervals.Count(i => i.Booking != Excluded && i.Span.Contains(hour));

        public override string ToString() =>
            $"fleet {{{Fleet}}} intervals {string.Join(' ', Intervals)} restrictions {{{Restrictions}}} window {Window} excluded {Excluded}";
    }

    private sealed record ReferenceCheck(bool IsAvailable, int MinimumRemaining, HourSpan? FirstConflict, AvailabilityConflictReason Reason);

    private sealed record BookingSpec(int Number, HourSpan Span, int? Vehicle)
    {
        public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"b{Number}{Span}{(Vehicle is { } v ? $"@v{v + 1}" : string.Empty)}");
    }

    private sealed record AssignmentScenario(FleetSpec Fleet, IReadOnlyList<BookingSpec> Bookings)
    {
        /// <summary>A vehicle index at or beyond the fleet size stands for a vehicle of another category.</summary>
        public IReadOnlyList<OccupancyInterval> ToIntervals() =>
            [.. Bookings.Select(b => Booking(b.Number, b.Span.ToPeriod(), b.Vehicle is { } v ? v + 1 : null))];

        public override string ToString() => $"fleet {{{Fleet}}} bookings {string.Join(' ', Bookings)}";
    }
}
