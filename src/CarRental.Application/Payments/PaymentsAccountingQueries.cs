using CarRental.Application.Common.Persistence;
using CarRental.Application.Payments.Contracts;
using CarRental.Domain.Common;
using CarRental.Domain.Payments;
using Microsoft.EntityFrameworkCore;

namespace CarRental.Application.Payments;

/// <summary>
/// <see cref="IPaymentsAccountingQueries"/>: no-tracking projections of the payments and deposits of an agency over a
/// period (a month holds a few hundred of them).
/// </summary>
internal sealed class PaymentsAccountingQueries(IAppDbContext db) : IPaymentsAccountingQueries
{
    public async Task<IReadOnlyList<SettledPaymentInfo>> GetSettledPaymentsAsync(Guid agencyId, DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
    {
        if (to < from)
        {
            return [];
        }

        var start = TunisTime.StartOfDay(from);
        var end = TunisTime.StartOfDay(to.AddDays(1));
        var payments = await db.Set<Payment>()
            .AsNoTracking()
            .Where(p => p.AgencyId == agencyId && p.CapturedAtUtc != null && p.CapturedAtUtc >= start && p.CapturedAtUtc < end)
            .Select(p => new SettledRow(p.Id, p.BookingId, p.Direction, p.Method, p.CapturedAmount, p.CapturedAtUtc!.Value, p.ExternalReference, p.OriginalPaymentId, p.CashSessionId))
            .ToListAsync(cancellationToken);
        return [.. payments.Where(p => p.Amount.IsPositive).OrderBy(p => p.At).ThenBy(p => p.Id).Select(ToInfo)];
    }

    public async Task<IReadOnlyList<DepositMovementInfo>> GetDepositMovementsAsync(Guid agencyId, DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
    {
        if (to < from)
        {
            return [];
        }

        var start = TunisTime.StartOfDay(from);
        var end = TunisTime.StartOfDay(to.AddDays(1));
        var deposits = await db.Set<DepositHold>()
            .AsNoTracking()
            .Where(d => d.AgencyId == agencyId
                        && ((d.TakenAtUtc >= start && d.TakenAtUtc < end) || (d.SettledAtUtc != null && d.SettledAtUtc >= start && d.SettledAtUtc < end)))
            .Select(d => new
            {
                d.Id,
                d.BookingId,
                d.Mode,
                d.Status,
                d.Amount,
                d.RetainedAmount,
                d.TakenAtUtc,
                d.SettledAtUtc,
                d.ExternalReference,
                d.TakenInCashSessionId,
                d.SettledInCashSessionId,
            })
            .ToListAsync(cancellationToken);

        var movements = new List<DepositMovementInfo>();
        foreach (var deposit in deposits)
        {
            if (deposit.TakenAtUtc >= start && deposit.TakenAtUtc < end)
            {
                movements.Add(new DepositMovementInfo(
                    deposit.Id,
                    deposit.BookingId,
                    deposit.Mode.ToInfo(),
                    DepositMovementKindInfo.Taken,
                    deposit.Amount,
                    Money.Zero,
                    Money.Zero,
                    TunisTime.DateOf(deposit.TakenAtUtc),
                    deposit.TakenAtUtc,
                    deposit.ExternalReference,
                    deposit.TakenInCashSessionId));
            }

            if (deposit.Status != DepositStatus.Held && deposit.SettledAtUtc is { } settledAt && settledAt >= start && settledAt < end)
            {
                movements.Add(new DepositMovementInfo(
                    deposit.Id,
                    deposit.BookingId,
                    deposit.Mode.ToInfo(),
                    DepositMovementKindInfo.Settled,
                    deposit.Amount,
                    deposit.RetainedAmount,
                    deposit.Amount - deposit.RetainedAmount,
                    TunisTime.DateOf(settledAt),
                    settledAt,
                    deposit.ExternalReference,
                    deposit.SettledInCashSessionId));
            }
        }

        return [.. movements.OrderBy(m => m.AtUtc).ThenBy(m => m.DepositId).ThenBy(m => m.Kind)];
    }

    public async Task<IReadOnlyList<OnlinePaymentStateInfo>> GetOnlinePaymentsAsync(Guid agencyId, DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
    {
        if (to < from)
        {
            return [];
        }

        var start = TunisTime.StartOfDay(from);
        var end = TunisTime.StartOfDay(to.AddDays(1));
        var payments = await db.Set<Payment>()
            .AsNoTracking()
            .Where(p => p.AgencyId == agencyId
                        && p.Method == PaymentMethod.ClicToPay
                        && ((p.RecordedAtUtc >= start && p.RecordedAtUtc < end) || (p.CapturedAtUtc != null && p.CapturedAtUtc >= start && p.CapturedAtUtc < end)))
            .ToListAsync(cancellationToken);
        return
        [
            .. payments
                .OrderBy(p => p.RecordedAtUtc)
                .ThenBy(p => p.Id)
                .Select(p => new OnlinePaymentStateInfo(
                    p.Id,
                    p.BookingId,
                    p.Direction.ToInfo(),
                    p.Status.ToInfo(),
                    p.Amount,
                    p.CapturedAmount,
                    p.RefundedAmount,
                    p.OrderNumber,
                    p.ExternalReference,
                    p.GatewayStatusCode,
                    p.GatewayAttention is { } attention ? (GatewayAttentionInfo)(int)attention : null,
                    p.OriginalPaymentId,
                    p.RecordedAtUtc,
                    p.CapturedAtUtc)),
        ];
    }

    public async Task<IReadOnlyList<SettledPaymentInfo>> GetCashPaymentsOfSessionsAsync(IReadOnlyCollection<Guid> sessionIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sessionIds);
        if (sessionIds.Count == 0)
        {
            return [];
        }

        var ids = sessionIds.Distinct().ToList();
        var payments = await db.Set<Payment>()
            .AsNoTracking()
            .Where(p => p.CashSessionId != null && ids.Contains(p.CashSessionId.Value) && p.CapturedAtUtc != null)
            .Select(p => new SettledRow(p.Id, p.BookingId, p.Direction, p.Method, p.CapturedAmount, p.CapturedAtUtc!.Value, p.ExternalReference, p.OriginalPaymentId, p.CashSessionId))
            .ToListAsync(cancellationToken);
        return [.. payments.OrderBy(p => p.At).ThenBy(p => p.Id).Select(ToInfo)];
    }

    private static SettledPaymentInfo ToInfo(SettledRow row) => new(
        row.Id,
        row.BookingId,
        row.Direction.ToInfo(),
        row.Method.ToInfo(),
        row.Amount,
        TunisTime.DateOf(row.At),
        row.At,
        row.ExternalReference,
        row.OriginalPaymentId,
        row.CashSessionId);

    private sealed record SettledRow(
        Guid Id,
        Guid BookingId,
        PaymentDirection Direction,
        PaymentMethod Method,
        Money Amount,
        DateTimeOffset At,
        string? ExternalReference,
        Guid? OriginalPaymentId,
        Guid? CashSessionId);
}
