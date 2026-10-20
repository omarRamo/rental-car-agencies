using CarRental.Domain.Common;

namespace CarRental.Domain.Bookings.Availability;

/// <summary>
/// A vehicle of the category with the windows during which it can be rented (Fleet contract: in service, before its
/// sale, covered by every mandatory document, outside maintenance blocks). Windows are normalized: sorted, and
/// overlapping or touching windows merged, so a vehicle never counts twice and "covered by the windows" means
/// "contained in one window".
/// </summary>
public sealed class RentableVehicle
{
    public RentableVehicle(Guid id, string plateNumber, IEnumerable<Period> windows)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("A vehicle id is required.", nameof(id));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(plateNumber);
        ArgumentNullException.ThrowIfNull(windows);

        Id = id;
        PlateNumber = plateNumber;
        Windows = Merge(windows);
    }

    public Guid Id { get; }

    /// <summary>Registration plate, used to break ties between equivalent vehicles.</summary>
    public string PlateNumber { get; }

    /// <summary>Sorted, disjoint and non-touching rentable windows.</summary>
    public IReadOnlyList<Period> Windows { get; }

    /// <summary>The window that contains the whole <paramref name="period"/>, or <see langword="null"/>.</summary>
    public Period? WindowContaining(Period period)
    {
        ArgumentNullException.ThrowIfNull(period);

        // Windows are sorted and disjoint: the candidate is the last window starting at or before the period.
        var low = 0;
        var high = Windows.Count - 1;
        Period? candidate = null;
        while (low <= high)
        {
            var middle = low + ((high - low) / 2);
            if (Windows[middle].Start <= period.Start)
            {
                candidate = Windows[middle];
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        return candidate is not null && candidate.Contains(period) ? candidate : null;
    }

    /// <summary>Whether one window contains the whole <paramref name="period"/>.</summary>
    public bool IsRentableFor(Period period) => WindowContaining(period) is not null;

    public override string ToString() => $"{PlateNumber} ({Id})";

    /// <summary>One entry per vehicle id; the windows of duplicated entries are merged (a vehicle never counts twice).</summary>
    internal static IReadOnlyList<RentableVehicle> Distinct(IEnumerable<RentableVehicle> vehicles)
    {
        ArgumentNullException.ThrowIfNull(vehicles);

        var byId = new Dictionary<Guid, RentableVehicle>();
        var order = new List<Guid>();
        foreach (var vehicle in vehicles)
        {
            ArgumentNullException.ThrowIfNull(vehicle, nameof(vehicles));
            if (byId.TryGetValue(vehicle.Id, out var existing))
            {
                byId[vehicle.Id] = new RentableVehicle(existing.Id, existing.PlateNumber, existing.Windows.Concat(vehicle.Windows));
            }
            else
            {
                byId.Add(vehicle.Id, vehicle);
                order.Add(vehicle.Id);
            }
        }

        return [.. order.Select(id => byId[id])];
    }

    private static Period[] Merge(IEnumerable<Period> windows)
    {
        var sorted = windows
            .Select(window => window ?? throw new ArgumentNullException(nameof(windows), "A window cannot be null."))
            .OrderBy(window => window.Start)
            .ToList();

        var merged = new List<Period>(sorted.Count);
        foreach (var window in sorted)
        {
            if (merged.Count > 0 && window.Start <= merged[^1].End)
            {
                var last = merged[^1];
                if (window.End > last.End)
                {
                    merged[^1] = Period.Create(last.Start, window.End).Value;
                }
            }
            else
            {
                merged.Add(window);
            }
        }

        return [.. merged];
    }
}
