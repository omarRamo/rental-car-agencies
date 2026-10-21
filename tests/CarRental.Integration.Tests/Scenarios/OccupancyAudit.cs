using CarRental.Application.Common.Security;
using CarRental.Application.Referential.Contracts;
using CarRental.Domain.Bookings;
using CarRental.Domain.Bookings.Availability;
using CarRental.Domain.Common;
using CarRental.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CarRental.Integration.Tests.Scenarios;

/// <summary>A stored rental file and the period it occupies now, preparation buffer included.</summary>
internal sealed record OccupiedFile(Booking Booking, Period Period);

/// <summary>
/// Invariants 1 and 2 (PROMPT §6) checked on the stored rows, independently of the availability engine: the occupancy
/// of every file is recomputed from its status, dates and vehicle with the buffer of the agency, then counted.
/// </summary>
internal static class OccupancyAudit
{
    public static async Task<IReadOnlyList<OccupiedFile>> LoadAsync(HighSeasonHost host, Guid agencyId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(host);
        await using var scope = host.CreateScope(UserContextSnapshot.System("system:audit"));
        var buffer = await scope.ServiceProvider.GetRequiredService<ISettingsReader>().GetAsync(WellKnownSettings.PreparationBuffer, agencyId, cancellationToken);
        var basis = new OccupancyBasis(host.Clock.GetUtcNow(), buffer);
        var files = await scope.ServiceProvider.GetRequiredService<CarRentalDbContext>().Set<Booking>()
            .AsNoTracking()
            .Where(b => b.AgencyId == agencyId)
            .ToListAsync(cancellationToken);
        return
        [
            .. files
                .Select(file => (File: file, Period: basis.PeriodOf(file.ToOccupancy())))
                .Where(x => x.Period is not null)
                .Select(x => new OccupiedFile(x.File, x.Period!)),
        ];
    }

    /// <summary>No vehicle carries two files at once, buffer included (invariant 1), past and future alike.</summary>
    public static void NoVehicleCarriesTwoFiles(IEnumerable<OccupiedFile> files)
    {
        foreach (var vehicle in files.Where(f => f.Booking.AssignedVehicleId is not null).GroupBy(f => f.Booking.AssignedVehicleId))
        {
            var periods = vehicle.OrderBy(f => f.Period.Start).ToList();
            for (var i = 1; i < periods.Count; i++)
            {
                periods[i].Period.Overlaps(periods[i - 1].Period)
                    .ShouldBeFalse($"{periods[i - 1].Booking.Number} and {periods[i].Booking.Number} share a vehicle at the same time.");
            }
        }
    }

    /// <summary>The largest number of files a category carries at one instant (sweep of the period bounds).</summary>
    public static int MaxConcurrency(IEnumerable<Period> periods)
    {
        var edges = periods
            .SelectMany(p => new[] { (At: p.Start, Delta: 1), (At: p.End, Delta: -1) })
            .OrderBy(e => e.At)
            .ThenBy(e => e.Delta)
            .ToList();
        var current = 0;
        var max = 0;
        foreach (var edge in edges)
        {
            current += edge.Delta;
            max = Math.Max(max, current);
        }

        return max;
    }
}
