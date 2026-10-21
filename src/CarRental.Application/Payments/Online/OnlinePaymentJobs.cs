using CarRental.Application.Common.BackgroundJobs;
using CarRental.Application.Common.Persistence;
using CarRental.Domain.Payments;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CarRental.Application.Payments.Online;

/// <summary>
/// Catch-up of the online payments every 15 minutes (SPEC §9, ADR-0039): reads again the status of the payments still
/// pending, or with a capture, a reversal or a refund not confirmed, recorded more than 2 minutes ago (customers who
/// close the tab before the return), applies the confirmed statuses, closes the unpaid orders past their validity
/// (payment links included — TODO(spec): no operation to cancel an unpaid order at the gateway is known), sends again the
/// releases left without answer and flags what stays unconfirmed. Orders closed as expired are read for a day more
/// (late payments). A pending payment still unresolved a day after its validity (status code unknown, gateway silent)
/// is left to the daily reconciliation, so that it never crowds out the newer ones; the newest come first. Each payment
/// is processed on its own.
/// </summary>
internal sealed partial class OnlinePaymentCatchUpJob(
    IAppDbContext db,
    OnlinePayments online,
    IClicToPayGateway gateway,
    TimeProvider timeProvider,
    ILogger<OnlinePaymentCatchUpJob> logger) : IRecurringJob
{
    /// <summary>Most payments processed by one run (a run takes the newest first; the next run, 15 minutes later, continues).</summary>
    public const int MaxPerRun = 500;

    /// <summary>Payments younger than this are left to the return of the browser.</summary>
    public static readonly TimeSpan MinimumAge = TimeSpan.FromMinutes(2);

    /// <summary>Older payments are left to the daily reconciliation and the back office.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromDays(30);

    /// <summary>After its validity, an order is still read this long (late payment, unknown status) before the reconciliation takes over.</summary>
    public static readonly TimeSpan AfterValidity = TimeSpan.FromHours(24);

    public static string JobId => "payments.online_catch_up";

    public static string CronExpression => "*/15 * * * *";

    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        if (!gateway.IsEnabled)
        {
            return;
        }

        var now = timeProvider.GetUtcNow();
        var before = now - MinimumAge;
        var since = now - Window;
        var lately = now - AfterValidity;
        var incoming = await db.Set<Payment>()
            .AsNoTracking()
            .Where(p => p.Method == PaymentMethod.ClicToPay && p.Direction == PaymentDirection.Incoming
                        && p.RecordedAtUtc <= before && p.RecordedAtUtc >= since
                        && (p.PendingGatewayOperation != null
                            || (p.Status == PaymentStatus.Pending && (p.OnlineExpiresAtUtc == null || p.OnlineExpiresAtUtc >= lately))
                            || (p.Status == PaymentStatus.Cancelled && p.ExpiredAtUtc != null && p.ExpiredAtUtc >= lately)))
            .OrderByDescending(p => p.RecordedAtUtc)
            .Select(p => p.Id)
            .Take(MaxPerRun)
            .ToListAsync(cancellationToken);
        var refunded = await db.Set<Payment>()
            .AsNoTracking()
            .Where(r => r.Method == PaymentMethod.ClicToPay && r.Direction == PaymentDirection.Refund && r.Status == PaymentStatus.Pending
                        && r.PendingGatewayOperation == PendingGatewayOperation.Refund && r.PendingGatewaySinceUtc <= before
                        && r.OriginalPaymentId != null)
            .OrderByDescending(r => r.PendingGatewaySinceUtc)
            .Select(r => r.OriginalPaymentId!.Value)
            .Take(MaxPerRun)
            .ToListAsync(cancellationToken);

        foreach (var paymentId in incoming.Concat(refunded).Distinct())
        {
            try
            {
                await online.CatchUpAsync(paymentId, cancellationToken);
            }
#pragma warning disable CA1031 // One payment that cannot be processed must not stop the others; the next run retries it.
            catch (Exception exception) when (exception is not OperationCanceledException)
#pragma warning restore CA1031
            {
                LogFailed(exception, paymentId);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Catch-up of online payment {PaymentId} failed; the next run retries it.")]
    private partial void LogFailed(Exception exception, Guid paymentId);
}

/// <summary>
/// Daily reconciliation of the online payments with the gateway (SPEC §9): the status of every online payment of the
/// last days, newest first, is read and compared with the recorded one (status, captured and refunded amounts when
/// reported) — including the pending ones the catch-up left aside; differences are flagged for the back office.
/// TODO(spec): no merchant statement format of SMT is known yet, so the statement itself is not imported (settlements
/// and commissions are reconciled in Phase 3).
/// </summary>
internal sealed partial class OnlinePaymentReconciliationJob(
    IAppDbContext db,
    OnlinePayments online,
    IClicToPayGateway gateway,
    TimeProvider timeProvider,
    ILogger<OnlinePaymentReconciliationJob> logger) : IRecurringJob
{
    public const int BatchSize = 500;

    /// <summary>Online payments recorded in this window are compared each night.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromDays(7);

    public static string JobId => "payments.online_reconciliation";

    /// <summary>Every day at 04:30, Tunis time (quiet hours).</summary>
    public static string CronExpression => "30 4 * * *";

    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        if (!gateway.IsEnabled)
        {
            return;
        }

        var since = timeProvider.GetUtcNow() - Window;
        var ids = await db.Set<Payment>()
            .AsNoTracking()
            .Where(p => p.Method == PaymentMethod.ClicToPay && p.Direction == PaymentDirection.Incoming && p.RecordedAtUtc >= since)
            .OrderByDescending(p => p.RecordedAtUtc)
            .Select(p => p.Id)
            .Take(BatchSize)
            .ToListAsync(cancellationToken);

        var unreadable = 0;
        foreach (var paymentId in ids)
        {
            try
            {
                if ((await online.ReconcileAsync(paymentId, cancellationToken)).IsFailure)
                {
                    unreadable++;
                }
            }
#pragma warning disable CA1031 // One payment that cannot be compared must not stop the others.
            catch (Exception exception) when (exception is not OperationCanceledException)
#pragma warning restore CA1031
            {
                unreadable++;
                LogFailed(exception, paymentId);
            }
        }

        LogDone(ids.Count, unreadable);
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Reconciliation of online payment {PaymentId} failed.")]
    private partial void LogFailed(Exception exception, Guid paymentId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Online payments reconciled: {Count} compared, {Unreadable} could not be read.")]
    private partial void LogDone(int count, int unreadable);
}
