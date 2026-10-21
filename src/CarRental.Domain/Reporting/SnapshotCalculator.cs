using CarRental.Domain.Common;

namespace CarRental.Domain.Reporting;

/// <summary>
/// The fleet side of a vehicle-day (Fleet module): the vehicle is in the fleet that day, in that category; it could be
/// rented for at least part of the day according to the fleet windows; its depreciation of the day.
/// </summary>
public sealed record FleetVehicleDay(
    Guid VehicleId,
    string PlateNumber,
    string VehicleName,
    DateOnly Day,
    Guid CategoryId,
    string CategoryCode,
    bool IsRentable,
    Money Depreciation);

/// <summary>The vehicle carrying a booking from <see cref="Since"/> (<see langword="null"/>: from the start of the rental).</summary>
public sealed record CarrierSpan(Guid VehicleId, DateTimeOffset? Since);

/// <summary>
/// A booking whose revenue counts (ADR-0045): its rental revenue and commission (excluding VAT) are spread evenly over
/// its <see cref="Days"/> rented days from <see cref="PickupAt"/> (planned pick-up), each day going to the vehicle that
/// carried the booking at the start of that rented day.
/// </summary>
public sealed record RevenueRental(
    Guid BookingId,
    string Number,
    SalesChannel Channel,
    DateTimeOffset PickupAt,
    int Days,
    Money Revenue,
    Money Commission,
    IReadOnlyList<CarrierSpan> Vehicles);

/// <summary>A direct cost of a vehicle covering the Tunis days <see cref="From"/> to <see cref="To"/> (both included).</summary>
public sealed record VehicleCostItem(Guid VehicleId, Money Amount, DateOnly From, DateOnly To);

/// <summary>The share of a rental in a vehicle-day.</summary>
public sealed record RentalShare(Guid BookingId, string BookingNumber, SalesChannel Channel, DateTimeOffset PickupAt, Money Revenue, Money Commission);

/// <summary>
/// The snapshot rows of a range of days and one <see cref="SnapshotDay"/> per day of the range, which keeps the revenue
/// that no vehicle-day could carry (a counted booking without vehicle, or a rented day when its vehicle was not in the
/// fleet): reported apart, never silently dropped.
/// </summary>
public sealed record SnapshotComputation(IReadOnlyList<VehicleDaySnapshot> Rows, IReadOnlyList<SnapshotDay> Days)
{
    public Money UnattributedRevenue => Days.Select(d => d.UnattributedRevenue).Sum();

    public int UnattributedDays => Days.Sum(d => d.UnattributedRentedDays);
}

/// <summary>
/// Pure computation of the daily snapshots of an agency over a range of Tunis days (ADR-0045). Every amount spread over
/// several days (rental revenue, commission, a cost covering a period) is spread over its whole period with
/// <see cref="DailySpread"/>, then the days of the range are taken: a day gets the same share whichever range is
/// computed, and the shares of a booking always add up to its revenue.
/// </summary>
public static class SnapshotCalculator
{
    public static SnapshotComputation Compute(
        Guid agencyId,
        DateOnly from,
        DateOnly to,
        IEnumerable<FleetVehicleDay> fleet,
        IEnumerable<RevenueRental> rentals,
        IEnumerable<VehicleCostItem> costs,
        DateTimeOffset computedAt)
    {
        ArgumentNullException.ThrowIfNull(fleet);
        ArgumentNullException.ThrowIfNull(rentals);
        ArgumentNullException.ThrowIfNull(costs);
        if (to < from)
        {
            throw new ArgumentException("The range ends before it starts.", nameof(to));
        }

        var fleetDays = new Dictionary<(Guid Vehicle, DateOnly Day), FleetVehicleDay>();
        foreach (var day in fleet.Where(d => d.Day >= from && d.Day <= to))
        {
            if (!fleetDays.TryAdd((day.VehicleId, day.Day), day))
            {
                throw new ArgumentException($"Vehicle {day.VehicleId} appears twice on {day.Day:O}.", nameof(fleet));
            }
        }

        var shares = new Dictionary<(Guid Vehicle, DateOnly Day), Dictionary<Guid, RentalShare>>();
        var unattributed = new Dictionary<DateOnly, (Money Revenue, int Days)>();
        foreach (var rental in rentals)
        {
            ArgumentNullException.ThrowIfNull(rental);
            if (rental.Days < 1)
            {
                continue;
            }

            for (var i = 0; i < rental.Days; i++)
            {
                var date = RentalDays.DateOf(rental.PickupAt, i);
                if (date < from || date > to)
                {
                    continue;
                }

                var revenue = DailySpread.ShareOf(rental.Revenue, rental.Days, i);
                var commission = DailySpread.ShareOf(rental.Commission, rental.Days, i);
                if (CarrierAt(rental.Vehicles, RentalDays.StartOf(rental.PickupAt, i)) is not { } vehicle
                    || !fleetDays.ContainsKey((vehicle, date)))
                {
                    var (amount, count) = unattributed.GetValueOrDefault(date);
                    unattributed[date] = (amount + revenue, count + 1);
                    continue;
                }

                if (!shares.TryGetValue((vehicle, date), out var ofDay))
                {
                    ofDay = [];
                    shares[(vehicle, date)] = ofDay;
                }

                // Two units of one booking on one date (a daylight saving shift) merge into one share.
                ofDay[rental.BookingId] = ofDay.TryGetValue(rental.BookingId, out var existing)
                    ? existing with { Revenue = existing.Revenue + revenue, Commission = existing.Commission + commission }
                    : new RentalShare(rental.BookingId, rental.Number, rental.Channel, rental.PickupAt, revenue, commission);
            }
        }

        var directCosts = new Dictionary<(Guid Vehicle, DateOnly Day), Money>();
        foreach (var cost in costs)
        {
            ArgumentNullException.ThrowIfNull(cost);
            var last = cost.To < cost.From ? cost.From : cost.To;
            var costDays = last.DayNumber - cost.From.DayNumber + 1;
            var first = cost.From > from ? cost.From : from;
            var end = last < to ? last : to;
            for (var date = first; date <= end; date = date.AddDays(1))
            {
                if (!fleetDays.ContainsKey((cost.VehicleId, date)))
                {
                    continue;
                }

                var share = DailySpread.ShareOf(cost.Amount, costDays, date.DayNumber - cost.From.DayNumber);
                directCosts[(cost.VehicleId, date)] = directCosts.GetValueOrDefault((cost.VehicleId, date)) + share;
            }
        }

        var rows = fleetDays.Values
            .OrderBy(d => d.PlateNumber, StringComparer.Ordinal)
            .ThenBy(d => d.VehicleId)
            .ThenBy(d => d.Day)
            .Select(d => VehicleDaySnapshot.Create(
                agencyId,
                d,
                shares.TryGetValue((d.VehicleId, d.Day), out var ofDay) ? [.. ofDay.Values.OrderBy(s => s.BookingId)] : [],
                directCosts.GetValueOrDefault((d.VehicleId, d.Day)),
                computedAt))
            .ToList();
        var vehiclesByDay = rows.GroupBy(r => r.Day).ToDictionary(g => g.Key, g => g.Count());
        var snapshotDays = new List<SnapshotDay>(to.DayNumber - from.DayNumber + 1);
        for (var date = from; date <= to; date = date.AddDays(1))
        {
            var (revenue, count) = unattributed.GetValueOrDefault(date);
            snapshotDays.Add(SnapshotDay.Create(agencyId, date, vehiclesByDay.GetValueOrDefault(date), revenue, count, computedAt));
        }

        return new SnapshotComputation(rows, snapshotDays);
    }

    /// <summary>
    /// The vehicle carrying the booking at <paramref name="instant"/>: the last span started at or before it (the first
    /// span when the booking was carried from the start, or when every known change is later).
    /// </summary>
    public static Guid? CarrierAt(IReadOnlyList<CarrierSpan> vehicles, DateTimeOffset instant)
    {
        ArgumentNullException.ThrowIfNull(vehicles);
        CarrierSpan? carrier = null;
        foreach (var span in vehicles.OrderBy(v => v.Since ?? DateTimeOffset.MinValue))
        {
            if (carrier is null || span.Since is null || span.Since <= instant)
            {
                carrier = span;
            }
        }

        return carrier?.VehicleId;
    }
}
