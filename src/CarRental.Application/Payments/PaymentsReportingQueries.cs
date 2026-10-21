using CarRental.Application.Common.Persistence;
using CarRental.Application.Payments.Contracts;
using CarRental.Domain.Payments;
using Microsoft.EntityFrameworkCore;

namespace CarRental.Application.Payments;

/// <summary>
/// <see cref="IPaymentsReportingQueries"/>: the amounts of the payments of the bookings, projected in batches of
/// <see cref="BatchSize"/> identifiers, then totalled like the booking summary (<see cref="PaymentMapping.TotalsOf(IEnumerable{PaymentAmounts})"/>).
/// </summary>
internal sealed class PaymentsReportingQueries(IAppDbContext db) : IPaymentsReportingQueries
{
    /// <summary>Identifiers per statement (well under the parameter limit of SQL Server).</summary>
    public const int BatchSize = 500;

    public async Task<IReadOnlyDictionary<Guid, PaymentTotals>> GetPaymentTotalsByBookingAsync(
        IReadOnlyCollection<Guid> bookingIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bookingIds);
        var amounts = new List<(Guid BookingId, PaymentAmounts Amounts)>();
        foreach (var batch in bookingIds.Distinct().Chunk(BatchSize))
        {
            var rows = await db.Set<Payment>()
                .AsNoTracking()
                .Where(p => batch.Contains(p.BookingId))
                .Select(p => new { p.BookingId, p.Direction, p.Status, p.Amount, p.CapturedAmount })
                .ToListAsync(cancellationToken);
            amounts.AddRange(rows.Select(p => (p.BookingId, new PaymentAmounts(p.Direction, p.Status, p.Amount, p.CapturedAmount))));
        }

        return amounts
            .GroupBy(a => a.BookingId)
            .ToDictionary(g => g.Key, g => PaymentMapping.TotalsOf(g.Select(a => a.Amounts)));
    }
}
