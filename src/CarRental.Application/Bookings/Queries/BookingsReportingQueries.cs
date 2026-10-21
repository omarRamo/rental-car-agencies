using System.Text.Json;
using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Common.Persistence;
using CarRental.Domain.Bookings;
using CarRental.Domain.Bookings.Quotes;
using CarRental.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace CarRental.Application.Bookings.Queries;

/// <summary>
/// <see cref="IBookingsReportingQueries"/>: no-tracking projections of the files, of their current quote (totals and
/// daily contribution lines) and of their timeline, each read in one statement filtered by the same sub-query (no list of
/// identifiers sent as parameters).
/// </summary>
internal sealed class BookingsReportingQueries(IAppDbContext db) : IBookingsReportingQueries
{
    public async Task<IReadOnlyList<BookingFactsInfo>> GetBookingsOverlappingAsync(
        Guid agencyId,
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken = default)
    {
        if (to < from)
        {
            return [];
        }

        var start = TunisTime.StartOfDay(from);
        var end = TunisTime.StartOfDay(to.AddDays(1));
        var bookings = db.Set<Booking>()
            .AsNoTracking()
            .Where(b => b.AgencyId == agencyId && b.Status != BookingStatus.Draft && b.PickupAt < end && b.ReturnAt > start);
        return [.. (await FactsAsync(bookings, cancellationToken)).OrderBy(f => f.PickupAt).ThenBy(f => f.Number, StringComparer.Ordinal)];
    }

    public async Task<IReadOnlyList<BookingFactsInfo>> GetRequestsSubmittedAsync(
        Guid agencyId,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken cancellationToken = default)
    {
        if (toUtc <= fromUtc)
        {
            return [];
        }

        var from = fromUtc.ToUniversalTime();
        var to = toUtc.ToUniversalTime();
        var bookings = db.Set<Booking>()
            .AsNoTracking()
            .Where(b => b.AgencyId == agencyId && b.SubmittedAtUtc != null && b.SubmittedAtUtc >= from && b.SubmittedAtUtc < to);
        return [.. (await FactsAsync(bookings, cancellationToken)).OrderBy(f => f.SubmittedAtUtc).ThenBy(f => f.Number, StringComparer.Ordinal)];
    }

    private async Task<List<BookingFactsInfo>> FactsAsync(IQueryable<Booking> bookings, CancellationToken cancellationToken)
    {
        var rows = await bookings
            .Select(b => new
            {
                b.Id,
                b.Number,
                b.AgencyId,
                b.Status,
                b.Channel,
                b.Attribution.Source,
                b.CategoryId,
                b.PickupAt,
                b.ReturnAt,
                b.ActualPickupAt,
                b.ActualReturnAt,
                b.CreatedAtUtc,
                b.SubmittedAtUtc,
                b.CurrentQuoteId,
                b.CurrentQuoteVersion,
                b.TotalInclVat,
                b.CancellationFee,
                b.AssignedVehicleId,
            })
            .ToListAsync(cancellationToken);
        if (rows.Count == 0)
        {
            return [];
        }

        var quoteIds = bookings.Select(b => b.CurrentQuoteId);
        var quotes = await db.Set<Quote>()
            .AsNoTracking()
            .Where(q => quoteIds.Contains(q.Id))
            .Select(q => new { q.Id, q.Version, q.Days, q.TotalExclVat, q.TotalVat, q.TotalInclVat })
            .ToDictionaryAsync(q => q.Id, cancellationToken);
        var contributions = (await db.Set<QuoteLine>()
                .AsNoTracking()
                .Where(l => l.Kind == QuoteLineKind.DailyContribution && quoteIds.Contains(l.QuoteId))
                .Select(l => new { l.QuoteId, l.ExclVat })
                .ToListAsync(cancellationToken))
            .GroupBy(l => l.QuoteId)
            .ToDictionary(g => g.Key, g => g.Select(l => l.ExclVat).Sum());
        var bookingIds = bookings.Select(b => b.Id);
        var events = (await db.Set<BookingEvent>()
                .AsNoTracking()
                .Where(e => bookingIds.Contains(e.BookingId))
                .Select(e => new TimelineRow(e.BookingId, e.Sequence, e.OccurredAtUtc, e.Type, e.StatusBefore, e.StatusAfter, e.Data))
                .ToListAsync(cancellationToken))
            .ToLookup(e => e.BookingId);

        return
        [
            .. rows.Select(b =>
            {
                var timeline = events[b.Id].OrderBy(e => e.Sequence).ToList();
                var quote = quotes.GetValueOrDefault(b.CurrentQuoteId);
                return new BookingFactsInfo(
                    b.Id,
                    b.Number,
                    b.AgencyId,
                    b.Status.ToInfo(),
                    b.Channel,
                    b.Source,
                    b.CategoryId,
                    b.PickupAt,
                    b.ReturnAt,
                    b.ActualPickupAt,
                    b.ActualReturnAt,
                    b.CreatedAtUtc,
                    b.SubmittedAtUtc,
                    quote is null
                        ? new BookingQuoteFactsInfo(b.CurrentQuoteId, b.CurrentQuoteVersion, 0, Money.Zero, Money.Zero, b.TotalInclVat, Money.Zero)
                        : new BookingQuoteFactsInfo(
                            quote.Id,
                            quote.Version ?? b.CurrentQuoteVersion,
                            quote.Days,
                            quote.TotalExclVat,
                            quote.TotalVat,
                            quote.TotalInclVat,
                            contributions.GetValueOrDefault(quote.Id)),
                    b.Status == BookingStatus.Cancelled ? b.CancellationFee ?? Money.Zero : null,
                    VehiclesOf(b.AssignedVehicleId, timeline),
                    [
                        .. timeline
                            .Where(e => e.StatusBefore != e.StatusAfter)
                            .Select(e => new BookingStatusChangeInfo(e.OccurredAtUtc, e.StatusBefore?.ToInfo(), e.StatusAfter.ToInfo())),
                    ]);
            }),
        ];
    }

    /// <summary>
    /// The vehicles that carried a file: before any swap, the assigned vehicle; each swap during the rental (timeline
    /// facts <c>previousVehicle</c> and <c>vehicle</c>) starts a new span. Assignments before the departure do not matter:
    /// only the vehicle that leaves carries rented days.
    /// </summary>
    private static List<BookingVehicleSpanInfo> VehiclesOf(Guid? assignedVehicleId, IEnumerable<TimelineRow> timeline)
    {
        var swaps = timeline.Where(e => e.Type == BookingEventType.VehicleSwapped).ToList();
        if (swaps.Count == 0)
        {
            return assignedVehicleId is { } assigned ? [new BookingVehicleSpanInfo(assigned, null)] : [];
        }

        // A swap always names the vehicle it replaces (a rental in progress has one): it carried the file until then.
        var spans = new List<BookingVehicleSpanInfo>();
        if (VehicleFact(swaps[0].Data, "previousVehicle") is { } initial)
        {
            spans.Add(new BookingVehicleSpanInfo(initial, null));
        }

        foreach (var swap in swaps)
        {
            if (VehicleFact(swap.Data, "vehicle") is { } vehicle)
            {
                spans.Add(new BookingVehicleSpanInfo(vehicle, swap.OccurredAtUtc));
            }
        }

        return spans;
    }

    private static Guid? VehicleFact(string? data, string key)
    {
        if (string.IsNullOrEmpty(data))
        {
            return null;
        }

        var facts = JsonSerializer.Deserialize<Dictionary<string, string>>(data);
        return facts is not null && facts.TryGetValue(key, out var value) && Guid.TryParse(value, out var id) ? id : null;
    }

    private sealed record TimelineRow(
        Guid BookingId,
        int Sequence,
        DateTimeOffset OccurredAtUtc,
        BookingEventType Type,
        BookingStatus? StatusBefore,
        BookingStatus StatusAfter,
        string? Data);
}
