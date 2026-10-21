using CarRental.Domain.Common;
using CarRental.Domain.Fleet;

namespace CarRental.Application.Fleet;

/// <summary>A status change of a vehicle (readable history, kind <c>StatusChanged</c>).</summary>
internal sealed record VehicleStatusChange(DateTimeOffset AtUtc, VehicleStatus To);

/// <summary>A category change of a vehicle (readable history, kind <c>CategoryChanged</c>).</summary>
internal sealed record VehicleCategoryChange(DateTimeOffset AtUtc, Guid From, Guid To);

/// <summary>The validity of a document, current or superseded: from its issue day (open when unknown) to its expiry day included.</summary>
internal sealed record DocumentValidity(VehicleDocumentType Type, DateOnly? IssuedOn, DateOnly? ExpiresOn);

/// <summary>What the past of a vehicle is made of, for the reports (ADR-0045).</summary>
internal sealed record VehicleHistoryProfile(
    DateOnly InServiceOn,
    DateTimeOffset? SoldAtUtc,
    VehicleStatus CurrentStatus,
    DateTimeOffset CurrentStatusSinceUtc,
    IReadOnlyList<VehicleStatusChange> StatusChanges,
    IReadOnlyList<DocumentValidity> Documents,
    IReadOnlyList<Period> MaintenanceBlocks,
    Guid CurrentCategoryId,
    IReadOnlyList<VehicleCategoryChange> CategoryChanges);

/// <summary>
/// The rentable windows of a vehicle judged on its history rather than on its current state: the rules of
/// <see cref="VehicleAvailability"/> (in service, not sold, maintenance blocks) applied with the documents valid on each
/// day (renewals included) and the periods spent in the <c>Maintenance</c> or <c>OutOfService</c> status, so that a past day
/// keeps its answer. On days to come it agrees with the availability engine: the current status blocks from its start,
/// without end, and the documents stop at their expiry.
/// </summary>
internal static class FleetHistory
{
    public static IReadOnlyList<Period> Windows(VehicleHistoryProfile profile, Period range)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(range);
        var blocks = profile.MaintenanceBlocks
            .Concat(StatusBlocks(profile))
            .Concat(ComplianceGaps(profile.Documents, range))
            .Select(block => block.Intersect(range))
            .OfType<Period>()
            .ToList();
        return VehicleAvailability.Windows(
            new VehicleAvailabilityProfile(profile.InServiceOn, VehicleStatus.Active, profile.SoldAtUtc, DateTimeOffset.MaxValue, blocks),
            range,
            range.Start);
    }

    /// <summary>Periods spent in a blocking status (<c>Maintenance</c>, <c>OutOfService</c>), the current one open-ended.</summary>
    public static IEnumerable<Period> StatusBlocks(VehicleHistoryProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var blocks = new List<Period>();
        DateTimeOffset? blockedSince = null;
        foreach (var change in profile.StatusChanges.OrderBy(c => c.AtUtc))
        {
            var blocking = IsBlocking(change.To);
            if (blocking && blockedSince is null)
            {
                blockedSince = change.AtUtc;
            }
            else if (!blocking && blockedSince is { } since)
            {
                Add(blocks, since, change.AtUtc);
                blockedSince = null;
            }
        }

        if (blockedSince is { } open)
        {
            // Still blocked at the end of the history: without end while the vehicle is, else until its current status.
            Add(blocks, open, IsBlocking(profile.CurrentStatus) ? DateTimeOffset.MaxValue : profile.CurrentStatusSinceUtc);
        }
        else if (IsBlocking(profile.CurrentStatus))
        {
            // History lines missing: the current blocking status starts when the vehicle says it did.
            Add(blocks, profile.CurrentStatusSinceUtc, DateTimeOffset.MaxValue);
        }

        return blocks;
    }

    /// <summary>
    /// Periods of <paramref name="range"/> when a mandatory document (insurance, road tax, technical inspection) was not
    /// valid: no document of the type covers them, from its issue day (any time before when unknown) to the end of its
    /// expiry day. A type without any document leaves the whole range uncovered.
    /// </summary>
    public static IEnumerable<Period> ComplianceGaps(IReadOnlyList<DocumentValidity> documents, Period range)
    {
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentNullException.ThrowIfNull(range);
        var gaps = new List<Period>();
        foreach (var type in VehicleCompliance.MandatoryTypes)
        {
            var cursor = range.Start;
            var coverage = documents
                .Where(d => d.Type == type && d.ExpiresOn is not null)
                .Select(d => (
                    Start: d.IssuedOn is { } issued ? TunisTime.StartOfDay(issued) : range.Start,
                    End: VehicleCompliance.EndOfTunisDay(d.ExpiresOn!.Value)))
                .Where(c => c.End > c.Start)
                .OrderBy(c => c.Start);
            foreach (var (start, end) in coverage)
            {
                if (start > cursor)
                {
                    Add(gaps, cursor, start < range.End ? start : range.End);
                }

                if (end > cursor)
                {
                    cursor = end;
                }

                if (cursor >= range.End)
                {
                    break;
                }
            }

            if (cursor < range.End)
            {
                Add(gaps, cursor, range.End);
            }
        }

        return gaps;
    }

    /// <summary>The category of the vehicle at <paramref name="instant"/>, from its category changes.</summary>
    public static Guid CategoryAt(VehicleHistoryProfile profile, DateTimeOffset instant)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var changes = profile.CategoryChanges.OrderBy(c => c.AtUtc).ToList();
        var category = changes.Count > 0 ? changes[0].From : profile.CurrentCategoryId;
        foreach (var change in changes.Where(c => c.AtUtc <= instant))
        {
            category = change.To;
        }

        return category;
    }

    private static bool IsBlocking(VehicleStatus status) => status is VehicleStatus.Maintenance or VehicleStatus.OutOfService;

    private static void Add(List<Period> periods, DateTimeOffset start, DateTimeOffset end)
    {
        if (end > start)
        {
            periods.Add(Period.Create(start, end).Value);
        }
    }
}
