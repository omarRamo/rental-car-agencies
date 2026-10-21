using CarRental.Application.Common.Persistence;
using CarRental.Domain.Bookings;
using CarRental.Domain.Bookings.Availability;
using CarRental.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace CarRental.Application.Bookings.Availability;

/// <summary>
/// <see cref="IOccupancyReader"/> on the rental files: a superset filtered in SQL (statuses that occupy, actual or
/// planned pick-up before the range end, planned or actual return after <c>range.Start − buffer</c> or still in
/// progress), then the exact intervals computed in memory with the basis of the caller (ADR-0025).
/// </summary>
/// <remarks>Reads fresh rows (no tracking) through the scoped context, hence inside the caller's transaction.</remarks>
internal sealed class OccupancyReader(IAppDbContext db) : IOccupancyReader
{
    private static readonly BookingStatus[] Occupying = [.. BookingStatusRules.Occupying];

    public async Task<IReadOnlyList<OccupancyInterval>> GetOccupancyAsync(
        Guid categoryId,
        Period range,
        Guid? excludedBookingId,
        OccupancyBasis basis,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(range);
        ArgumentNullException.ThrowIfNull(basis);

        var end = range.End;
        var earliestReturn = range.Start - basis.PreparationBuffer;
        var excluded = excludedBookingId ?? Guid.Empty;
        var rows = await db.Set<Booking>()
            .AsNoTracking()
            .Where(b => b.CategoryId == categoryId
                        && b.Id != excluded
                        && Occupying.Contains(b.Status)
                        && (b.PickupAt < end || (b.ActualPickupAt != null && b.ActualPickupAt < end))
                        && (b.ReturnAt > earliestReturn
                            || (b.ActualReturnAt != null && b.ActualReturnAt > earliestReturn)
                            || b.Status == BookingStatus.InProgress))
            .Select(b => new OccupancyRow(
                b.Id,
                b.Status,
                b.PickupAt,
                b.ReturnAt,
                b.ActualPickupAt,
                b.ActualReturnAt,
                b.AssignedVehicleId ?? b.GuaranteedVehicleId))
            .ToListAsync(cancellationToken);

        var intervals = new List<OccupancyInterval>(rows.Count);
        foreach (var row in rows)
        {
            if (basis.IntervalOf(row.ToOccupancy()) is { } interval && interval.Period.Overlaps(range))
            {
                intervals.Add(interval);
            }
        }

        return intervals;
    }

    private sealed record OccupancyRow(
        Guid Id,
        BookingStatus Status,
        DateTimeOffset PickupAt,
        DateTimeOffset ReturnAt,
        DateTimeOffset? ActualPickupAt,
        DateTimeOffset? ActualReturnAt,
        Guid? VehicleId)
    {
        public BookingOccupancy ToOccupancy() => new(
            Id,
            Status.ToOccupancyKind(),
            Period.Create(PickupAt, ReturnAt).Value,
            ActualPickupAt,
            ActualReturnAt,
            VehicleId);
    }
}
