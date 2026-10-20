using CarRental.Application.Common.Persistence;
using CarRental.Application.Payments.Contracts;
using CarRental.Domain.Common;
using CarRental.Domain.Payments;
using Microsoft.EntityFrameworkCore;

namespace CarRental.Application.Payments;

/// <summary>
/// Read API of the module for the other modules. Amounts are projected, then summed in memory: a booking has a handful of
/// payments, and a month of the agency a few hundred.
/// </summary>
internal sealed class PaymentsQueries(IAppDbContext db) : IPaymentsQueries
{
    public async Task<BookingPaymentSummary> GetBookingSummaryAsync(Guid bookingId, CancellationToken cancellationToken = default)
    {
        var payments = await db.Set<Payment>()
            .AsNoTracking()
            .Where(p => p.BookingId == bookingId)
            .Select(p => new PaymentAmounts(p.Direction, p.Status, p.Amount, p.CapturedAmount))
            .ToListAsync(cancellationToken);
        var deposits = await db.Set<DepositHold>()
            .AsNoTracking()
            .Where(d => d.BookingId == bookingId)
            .Select(d => new DepositAmounts(d.Status, d.Amount, d.RetainedAmount))
            .ToListAsync(cancellationToken);
        return new BookingPaymentSummary(bookingId, PaymentMapping.TotalsOf(payments), PaymentMapping.TotalsOf(deposits));
    }

    public async Task<IReadOnlyList<DailyPaymentTotal>> GetDailyTotalsByMethodAsync(
        DateOnly from,
        DateOnly to,
        Guid? agencyId = null,
        CancellationToken cancellationToken = default)
    {
        var start = TunisTime.StartOfDay(from);
        var end = TunisTime.StartOfDay(to.AddDays(1));

        // Settled money only: CapturedAtUtc is set once the money was received (or paid back, for a refund).
        var query = db.Set<Payment>()
            .AsNoTracking()
            .Where(p => p.CapturedAtUtc != null && p.CapturedAtUtc >= start && p.CapturedAtUtc < end);
        if (agencyId is { } agency)
        {
            query = query.Where(p => p.AgencyId == agency);
        }

        var settled = await query
            .Select(p => new { p.Direction, p.Method, p.CapturedAmount, CapturedAtUtc = p.CapturedAtUtc!.Value })
            .ToListAsync(cancellationToken);
        return settled
            .GroupBy(p => (Date: TunisTime.DateOf(p.CapturedAtUtc), p.Method))
            .OrderBy(g => g.Key.Date)
            .ThenBy(g => g.Key.Method)
            .Select(g => new DailyPaymentTotal(
                g.Key.Date,
                g.Key.Method.ToInfo(),
                g.Where(p => p.Direction == PaymentDirection.Incoming).Select(p => p.CapturedAmount).Sum(),
                g.Where(p => p.Direction == PaymentDirection.Refund).Select(p => p.CapturedAmount).Sum(),
                g.Count(p => p.Direction == PaymentDirection.Incoming),
                g.Count(p => p.Direction == PaymentDirection.Refund)))
            .ToList();
    }

    public async Task<DepositLiabilities> GetOpenDepositLiabilitiesAsync(Guid? agencyId = null, CancellationToken cancellationToken = default)
    {
        var query = db.Set<DepositHold>().AsNoTracking().Where(d => d.Status == DepositStatus.Held);
        if (agencyId is { } agency)
        {
            query = query.Where(d => d.AgencyId == agency);
        }

        var held = await query.Select(d => new { d.Mode, d.Amount }).ToListAsync(cancellationToken);
        var byMode = held
            .GroupBy(d => d.Mode)
            .OrderBy(g => g.Key)
            .Select(g => new DepositLiabilityByMode(g.Key.ToInfo(), g.Select(d => d.Amount).Sum(), g.Count()))
            .ToList();
        return new DepositLiabilities(held.Select(d => d.Amount).Sum(), held.Count, byMode);
    }
}
