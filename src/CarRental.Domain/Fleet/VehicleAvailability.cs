using CarRental.Domain.Common;

namespace CarRental.Domain.Fleet;

/// <summary>What the availability windows of a vehicle depend on (docs/conception/phase-1-exploitation.md §3).</summary>
/// <param name="InServiceOn">Tunis date the vehicle was put in service: rentable from the start of that day.</param>
/// <param name="Status">Current status; <c>Maintenance</c> and <c>OutOfService</c> block from now on, without end,
/// unless a dated block contains the current instant.</param>
/// <param name="SoldAtUtc">Instant of the sale: the vehicle is unavailable afterwards.</param>
/// <param name="CompliantUntil">End of the period covered by the mandatory documents; <see langword="null"/> = not compliant, no window.</param>
/// <param name="MaintenanceBlocks">Dated maintenance blocks <c>[start, end)</c>, in any order, possibly overlapping.</param>
public sealed record VehicleAvailabilityProfile(
    DateOnly InServiceOn,
    VehicleStatus Status,
    DateTimeOffset? SoldAtUtc,
    DateTimeOffset? CompliantUntil,
    IReadOnlyList<Period> MaintenanceBlocks);

/// <summary>
/// Pure computation of the rentable windows of a vehicle. Capacity of a category at instant t = number of its vehicles
/// having a window that contains t.
/// </summary>
public static class VehicleAvailability
{
    /// <summary>
    /// Sub-periods of <paramref name="range"/> during which the vehicle can be rented, evaluated at <paramref name="now"/>:
    /// from the start of its in-service day, until the earliest of its sale and the end of its compliance, minus the
    /// maintenance blocks, minus <c>[now, ∞)</c> when the status blocks the vehicle and no dated block covers now.
    /// </summary>
    /// <returns>Half-open windows, ordered, disjoint (never touching) and inside <paramref name="range"/>; empty when the
    /// vehicle is never available in the range.</returns>
    public static IReadOnlyList<Period> Windows(VehicleAvailabilityProfile profile, Period range, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(range);
        ArgumentNullException.ThrowIfNull(profile.MaintenanceBlocks);

        if (profile.CompliantUntil is not { } compliantUntil
            || (profile.Status == VehicleStatus.Sold && profile.SoldAtUtc is null))
        {
            return [];
        }

        var start = Max(TunisTime.StartOfDay(profile.InServiceOn), range.Start);
        var end = Min(compliantUntil, range.End);
        if (profile.SoldAtUtc is { } soldAt)
        {
            end = Min(end, soldAt);
        }

        if (end <= start)
        {
            return [];
        }

        var blocks = profile.MaintenanceBlocks
            .Select(b => (b.Start, End: b.End))
            .ToList();
        if (profile.Status is VehicleStatus.Maintenance or VehicleStatus.OutOfService
            && !profile.MaintenanceBlocks.Any(b => b.Contains(now)))
        {
            blocks.Add((now, DateTimeOffset.MaxValue));
        }

        var windows = new List<Period>();
        var cursor = start;
        foreach (var (blockStart, blockEnd) in blocks.OrderBy(b => b.Start))
        {
            if (blockEnd <= cursor)
            {
                continue;
            }

            if (blockStart >= end)
            {
                break;
            }

            if (blockStart > cursor)
            {
                windows.Add(Period.Create(cursor, blockStart).Value);
            }

            cursor = blockEnd;
            if (cursor >= end)
            {
                break;
            }
        }

        if (cursor < end)
        {
            windows.Add(Period.Create(cursor, end).Value);
        }

        return windows;
    }

    private static DateTimeOffset Max(DateTimeOffset left, DateTimeOffset right) => left > right ? left : right;

    private static DateTimeOffset Min(DateTimeOffset left, DateTimeOffset right) => left < right ? left : right;
}
