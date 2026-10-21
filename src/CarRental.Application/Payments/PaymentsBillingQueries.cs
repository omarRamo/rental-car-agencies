using CarRental.Application.Common.Persistence;
using CarRental.Application.Payments.Contracts;
using CarRental.Domain.Payments;
using Microsoft.EntityFrameworkCore;

namespace CarRental.Application.Payments;

/// <summary>
/// <see cref="IPaymentsBillingQueries"/>: the payments of a booking projected like the back office lists, and the totals of
/// many bookings in batches of <see cref="BatchSize"/> identifiers, totalled like the booking summary.
/// </summary>
internal sealed class PaymentsBillingQueries(IAppDbContext db) : IPaymentsBillingQueries
{
    /// <summary>Identifiers per statement (well under the parameter limit of SQL Server).</summary>
    public const int BatchSize = 500;

    public async Task<BookingBillingPayments> GetBookingPaymentsAsync(Guid bookingId, CancellationToken cancellationToken = default)
    {
        var payments = await db.Set<Payment>()
            .AsNoTracking()
            .Where(p => p.BookingId == bookingId)
            .OrderBy(p => p.RecordedAtUtc)
            .ToListAsync(cancellationToken);
        var deposits = await db.Set<DepositHold>()
            .AsNoTracking()
            .Where(d => d.BookingId == bookingId)
            .Select(d => new DepositAmounts(d.Status, d.Amount, d.RetainedAmount))
            .ToListAsync(cancellationToken);
        return new BookingBillingPayments(
            bookingId,
            [.. payments.Select(p => p.ToInfo())],
            PaymentMapping.TotalsOf(payments.Select(p => p.AmountsOf())),
            PaymentMapping.TotalsOf(deposits));
    }

    public async Task<IReadOnlyDictionary<Guid, BookingPaymentSummary>> GetSummariesAsync(
        IReadOnlyCollection<Guid> bookingIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bookingIds);
        var payments = new List<(Guid BookingId, PaymentAmounts Amounts)>();
        var deposits = new List<(Guid BookingId, DepositAmounts Amounts)>();
        var ids = bookingIds.Distinct().ToList();
        foreach (var batch in ids.Chunk(BatchSize))
        {
            var paymentRows = await db.Set<Payment>()
                .AsNoTracking()
                .Where(p => batch.Contains(p.BookingId))
                .Select(p => new { p.BookingId, p.Direction, p.Status, p.Amount, p.CapturedAmount })
                .ToListAsync(cancellationToken);
            payments.AddRange(paymentRows.Select(p => (p.BookingId, new PaymentAmounts(p.Direction, p.Status, p.Amount, p.CapturedAmount))));
            var depositRows = await db.Set<DepositHold>()
                .AsNoTracking()
                .Where(d => batch.Contains(d.BookingId))
                .Select(d => new { d.BookingId, d.Status, d.Amount, d.RetainedAmount })
                .ToListAsync(cancellationToken);
            deposits.AddRange(depositRows.Select(d => (d.BookingId, new DepositAmounts(d.Status, d.Amount, d.RetainedAmount))));
        }

        var paymentsByBooking = payments.ToLookup(p => p.BookingId, p => p.Amounts);
        var depositsByBooking = deposits.ToLookup(d => d.BookingId, d => d.Amounts);
        return ids.ToDictionary(
            id => id,
            id => new BookingPaymentSummary(id, PaymentMapping.TotalsOf(paymentsByBooking[id]), PaymentMapping.TotalsOf(depositsByBooking[id])));
    }
}
