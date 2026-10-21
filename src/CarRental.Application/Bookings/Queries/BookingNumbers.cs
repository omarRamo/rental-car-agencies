using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Common.Persistence;
using CarRental.Domain.Bookings;
using Microsoft.EntityFrameworkCore;

namespace CarRental.Application.Bookings.Queries;

/// <summary><see cref="IBookingNumbers"/>: the numbers of rental files, read in batches.</summary>
internal sealed class BookingNumbers(IAppDbContext db) : IBookingNumbers
{
    /// <summary>Ids per query (well under the parameter limit of SQL Server).</summary>
    private const int BatchSize = 500;

    public async Task<IReadOnlyDictionary<Guid, string>> GetNumbersAsync(IReadOnlyCollection<Guid> bookingIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bookingIds);
        var numbers = new Dictionary<Guid, string>();
        foreach (var batch in bookingIds.Distinct().Chunk(BatchSize))
        {
            var ids = batch.ToList();
            var rows = await db.Set<Booking>()
                .AsNoTracking()
                .IgnoreAutoIncludes()
                .Where(b => ids.Contains(b.Id))
                .Select(b => new { b.Id, b.Number })
                .ToListAsync(cancellationToken);
            foreach (var row in rows)
            {
                numbers[row.Id] = row.Number;
            }
        }

        return numbers;
    }
}
