using System.Text.Json;
using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Common.Persistence;
using CarRental.Domain.Bookings;
using CarRental.Domain.Bookings.Quotes;
using CarRental.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace CarRental.Application.Bookings.Queries;

/// <summary>
/// <see cref="IBookingsBillingQueries"/>: no-tracking projections of the files, of their current quote lines and of the
/// vehicle swaps of their timeline (a swap during the rental moves the vehicle that carries it, ADR-0030).
/// </summary>
internal sealed class BookingsBillingQueries(IAppDbContext db) : IBookingsBillingQueries
{
    /// <summary>Statuses whose vehicle left the agency: the rentals that can incur a fine.</summary>
    private static readonly BookingStatus[] Rented = [BookingStatus.InProgress, BookingStatus.Returned, BookingStatus.Closed];

    public async Task<BookingBillingInfo?> GetBillingFactsAsync(Guid bookingId, CancellationToken cancellationToken = default)
    {
        var booking = await db.Set<Booking>()
            .AsNoTracking()
            .Where(b => b.Id == bookingId)
            .Select(b => new
            {
                b.Id,
                b.Number,
                b.AgencyId,
                b.Status,
                b.Channel,
                b.ChannelReference,
                b.PickupAt,
                b.ReturnAt,
                b.ActualPickupAt,
                b.ActualReturnAt,
                b.MainDriverId,
                b.SecondaryDriverId,
                b.PayerCustomerId,
                b.AssignedVehicleId,
                b.CancellationFee,
                b.StatusChangedAtUtc,
                b.CurrentQuoteId,
                b.CurrentQuoteVersion,
                b.TotalInclVat,
                b.Deposit,
            })
            .FirstOrDefaultAsync(cancellationToken);
        if (booking is null)
        {
            return null;
        }

        var quote = await db.Set<Quote>()
            .AsNoTracking()
            .Where(q => q.Id == booking.CurrentQuoteId)
            .Select(q => new { q.Id, q.Version, q.Days, q.TotalExclVat, q.TotalVat, q.TotalInclVat, q.Deposit, q.IncludesFranchiseWaiver })
            .FirstOrDefaultAsync(cancellationToken);
        var lines = await db.Set<QuoteLine>()
            .AsNoTracking()
            .Where(l => l.QuoteId == booking.CurrentQuoteId)
            .OrderBy(l => l.Position)
            .ToListAsync(cancellationToken);

        return new BookingBillingInfo(
            booking.Id,
            booking.Number,
            booking.AgencyId,
            booking.Status.ToInfo(),
            booking.Channel,
            booking.ChannelReference,
            booking.PickupAt,
            booking.ReturnAt,
            booking.ActualPickupAt,
            booking.ActualReturnAt,
            booking.MainDriverId,
            booking.SecondaryDriverId,
            booking.PayerCustomerId,
            booking.AssignedVehicleId,
            booking.CancellationFee,
            booking.StatusChangedAtUtc,
            new BookingQuoteBillingInfo(
                booking.CurrentQuoteId,
                quote?.Version ?? booking.CurrentQuoteVersion,
                quote?.Days ?? 0,
                quote?.TotalExclVat ?? Money.Zero,
                quote?.TotalVat ?? Money.Zero,
                quote?.TotalInclVat ?? booking.TotalInclVat,
                quote?.Deposit ?? booking.Deposit,
                quote?.IncludesFranchiseWaiver ?? false,
                [
                    .. lines.Select(l => new BookingQuoteLineInfo(
                        l.Position,
                        (BookingQuoteLineKindInfo)(int)l.Kind,
                        l.Code,
                        l.LabelKey,
                        l.LabelArguments,
                        l.Quantity,
                        l.UnitPrice,
                        l.ExclVat,
                        l.Vat,
                        l.InclVat,
                        l.VatRate)),
                ]));
    }

    public async Task<IReadOnlyList<VehicleRentalInfo>> FindRentalsOfVehicleAtAsync(Guid vehicleId, DateTimeOffset instant, CancellationToken cancellationToken = default)
    {
        var at = instant.ToUniversalTime();
        var spans = await SpansAsync(
            vehicleId,
            db.Set<Booking>().AsNoTracking().Where(b => Rented.Contains(b.Status)
                                                         && b.ActualPickupAt != null && b.ActualPickupAt <= at
                                                         && (b.ActualReturnAt == null || b.ActualReturnAt > at)),
            cancellationToken);
        return [.. spans.Where(s => s.CarriedFromUtc <= at && (s.CarriedUntilUtc is null || at < s.CarriedUntilUtc))];
    }

    public async Task<IReadOnlyList<VehicleRentalInfo>> GetRentalsOfVehicleAsync(
        Guid vehicleId,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken cancellationToken = default)
    {
        var from = fromUtc.ToUniversalTime();
        var to = toUtc.ToUniversalTime();
        if (to <= from)
        {
            return [];
        }

        var spans = await SpansAsync(
            vehicleId,
            db.Set<Booking>().AsNoTracking().Where(b => Rented.Contains(b.Status)
                                                         && b.ActualPickupAt != null && b.ActualPickupAt < to
                                                         && (b.ActualReturnAt == null || b.ActualReturnAt > from)),
            cancellationToken);
        return [.. spans.Where(s => s.CarriedFromUtc < to && (s.CarriedUntilUtc is null || s.CarriedUntilUtc > from)).OrderBy(s => s.CarriedFromUtc)];
    }

    public async Task<IReadOnlyList<BookingToInvoiceInfo>> GetFilesToInvoiceAsync(
        Guid agencyId,
        DateTimeOffset sinceUtc,
        int limit = 200,
        CancellationToken cancellationToken = default)
    {
        var since = sinceUtc.ToUniversalTime();
        var take = Math.Clamp(limit, 1, 1_000);
        var files = await db.Set<Booking>()
            .AsNoTracking()
            .Where(b => b.AgencyId == agencyId
                        && b.StatusChangedAtUtc >= since
                        && (b.Status == BookingStatus.Closed || (b.Status == BookingStatus.Cancelled && b.CancellationFee != null)))
            .OrderByDescending(b => b.StatusChangedAtUtc)
            .Select(b => new BookingToInvoiceInfo(
                b.Id,
                b.Number,
                (BookingStatusInfo)b.Status,
                b.StatusChangedAtUtc,
                b.PayerCustomerId,
                b.MainDriverId,
                b.TotalInclVat,
                b.CancellationFee))
            .ToListAsync(cancellationToken);

        // A free (or waived) cancellation records a zero fee: nothing to invoice.
        return [.. files.Where(f => f.Status == BookingStatusInfo.Closed || f.CancellationFee is { IsPositive: true }).Take(take)];
    }

    /// <summary>
    /// The spans of <paramref name="vehicleId"/> among <paramref name="bookings"/> (rentals that left): before any swap the
    /// assigned vehicle carries the whole rental; each swap during the rental (timeline facts <c>previousVehicle</c> and
    /// <c>vehicle</c>) ends the span of the vehicle it replaces and starts the span of the new one — the rule of the
    /// reporting facts (<see cref="BookingsReportingQueries"/>).
    /// </summary>
    private async Task<List<VehicleRentalInfo>> SpansAsync(Guid vehicleId, IQueryable<Booking> bookings, CancellationToken cancellationToken)
    {
        var rows = await bookings
            .Select(b => new
            {
                b.Id,
                b.Number,
                b.AgencyId,
                b.Status,
                b.AssignedVehicleId,
                ActualPickupAt = b.ActualPickupAt!.Value,
                b.ActualReturnAt,
                b.MainDriverId,
                b.SecondaryDriverId,
                b.PayerCustomerId,
            })
            .ToListAsync(cancellationToken);
        if (rows.Count == 0)
        {
            return [];
        }

        var ids = rows.Select(r => r.Id).ToList();
        var swaps = (await db.Set<BookingEvent>()
                .AsNoTracking()
                .Where(e => ids.Contains(e.BookingId) && e.Type == BookingEventType.VehicleSwapped)
                .Select(e => new { e.BookingId, e.Sequence, e.OccurredAtUtc, e.Data })
                .ToListAsync(cancellationToken))
            .ToLookup(e => e.BookingId);

        var spans = new List<VehicleRentalInfo>();
        foreach (var row in rows)
        {
            // (vehicle, from) in order; each span ends where the next one starts, the last one at the actual return.
            var carriers = new List<(Guid Vehicle, DateTimeOffset From)>();
            var ordered = swaps[row.Id].OrderBy(s => s.Sequence).ToList();
            if (ordered.Count == 0)
            {
                if (row.AssignedVehicleId is { } assigned)
                {
                    carriers.Add((assigned, row.ActualPickupAt));
                }
            }
            else
            {
                if (VehicleFact(ordered[0].Data, "previousVehicle") is { } initial)
                {
                    carriers.Add((initial, row.ActualPickupAt));
                }

                foreach (var swap in ordered)
                {
                    if (VehicleFact(swap.Data, "vehicle") is { } next)
                    {
                        carriers.Add((next, swap.OccurredAtUtc));
                    }
                }
            }

            for (var i = 0; i < carriers.Count; i++)
            {
                if (carriers[i].Vehicle != vehicleId)
                {
                    continue;
                }

                var until = i + 1 < carriers.Count ? carriers[i + 1].From : row.ActualReturnAt;
                spans.Add(new VehicleRentalInfo(
                    row.Id,
                    row.Number,
                    row.AgencyId,
                    row.Status.ToInfo(),
                    vehicleId,
                    carriers[i].From,
                    until,
                    row.MainDriverId,
                    row.SecondaryDriverId,
                    row.PayerCustomerId));
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
}
