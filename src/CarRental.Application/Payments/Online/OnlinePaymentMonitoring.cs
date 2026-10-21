using CarRental.Application.Common.Persistence;
using CarRental.Domain.Payments;
using Microsoft.EntityFrameworkCore;

namespace CarRental.Application.Payments.Online;

/// <summary>
/// What the online payments need from the back office, for the health entry <c>payments</c> (ADR-0060): payments
/// flagged by the gateway checks (<see cref="Payment.GatewayAttention"/>: unknown status, mismatch, late payment,
/// capture, reversal or refund not confirmed, operation refused — never a card declined by the bank), and gateway
/// operations still unconfirmed <see cref="OnlinePaymentMonitoring.UnconfirmedAlertAge"/> after they were sent (the
/// catch-up runs every 15 minutes and should have settled them).
/// </summary>
/// <param name="FlaggedCount">Online payments flagged and not yet marked as checked.</param>
/// <param name="UnconfirmedOperationCount">Captures, reversals or refunds sent more than an hour ago and still unconfirmed.</param>
/// <param name="OldestUnconfirmedSinceUtc">When the oldest of those operations was sent.</param>
public sealed record OnlinePaymentHealthStatistics(int FlaggedCount, int UnconfirmedOperationCount, DateTimeOffset? OldestUnconfirmedSinceUtc);

public static class OnlinePaymentMonitoring
{
    /// <summary>Four runs of the catch-up job: an operation still unconfirmed after this is stuck.</summary>
    public static readonly TimeSpan UnconfirmedAlertAge = TimeSpan.FromHours(1);

    /// <summary>The figures at <paramref name="now"/> (read without permission: technical health, counts only).</summary>
    public static async Task<OnlinePaymentHealthStatistics> ReadStatisticsAsync(IAppDbContext db, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        var online = db.Set<Payment>().AsNoTracking().Where(p => p.Method == PaymentMethod.ClicToPay);
        var flagged = await online.CountAsync(p => p.GatewayAttention != null, cancellationToken);
        var sentBefore = now - UnconfirmedAlertAge;
        var unconfirmed = await online
            .Where(p => p.PendingGatewayOperation != null && p.PendingGatewaySinceUtc <= sentBefore)
            .GroupBy(_ => 1)
            .Select(g => new { Count = g.Count(), Oldest = g.Min(p => p.PendingGatewaySinceUtc) })
            .FirstOrDefaultAsync(cancellationToken);
        return new OnlinePaymentHealthStatistics(flagged, unconfirmed?.Count ?? 0, unconfirmed?.Oldest);
    }
}
