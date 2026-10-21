using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Common.Persistence;
using CarRental.Application.Reporting.Contracts;
using CarRental.Domain.Bookings.Handover;
using CarRental.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace CarRental.Application.Bookings.Handover;

/// <summary>
/// The Reporting port of the charges due beyond the quote (ADR-0046) served by the return charges (ADR-0042): the total,
/// VAT included, of the completed return of each file, as <see cref="IHandoverQueries"/> gives it; nothing before the
/// return. Only the inspection heads and the charge amounts are read (no photos, damages or signatures).
/// </summary>
internal sealed class ReturnChargesReportingSource(IAppDbContext db) : IBookingChargesSource
{
    public async Task<IReadOnlyDictionary<Guid, Money>> GetChargesDueAsync(
        Guid agencyId,
        IReadOnlyCollection<Guid> bookingIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bookingIds);
        var charges = new Dictionary<Guid, Money>();

        // Chunks keep the SQL parameter lists short.
        foreach (var chunk in bookingIds.Distinct().Chunk(500))
        {
            var returns = await db.Set<Inspection>()
                .AsNoTracking()
                .IgnoreAutoIncludes()
                .Where(i => chunk.Contains(i.BookingId) && i.Kind == InspectionKind.Return)
                .Select(i => new { i.Id, i.BookingId, i.Status, i.StartedAtUtc })
                .ToListAsync(cancellationToken);

            // The latest return of a file counts, once completed (same rule as GetReturnChargesAsync).
            var completed = returns
                .GroupBy(i => i.BookingId)
                .Select(g => g.OrderByDescending(i => i.StartedAtUtc).First())
                .Where(i => i.Status == InspectionStatus.Completed)
                .ToDictionary(i => i.Id, i => i.BookingId);
            if (completed.Count == 0)
            {
                continue;
            }

            var inspectionIds = completed.Keys.ToList();
            var lines = await db.Set<ReturnCharge>()
                .AsNoTracking()
                .Where(c => inspectionIds.Contains(c.InspectionId))
                .Select(c => new { c.InspectionId, c.InclVat })
                .ToListAsync(cancellationToken);
            foreach (var inspection in lines.GroupBy(c => c.InspectionId))
            {
                var total = inspection.Select(c => c.InclVat).Sum();
                if (!total.IsZero)
                {
                    charges[completed[inspection.Key]] = total;
                }
            }
        }

        return charges;
    }
}
