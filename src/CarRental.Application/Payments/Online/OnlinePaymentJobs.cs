using CarRental.Application.Common.BackgroundJobs;
using CarRental.Application.Common.Persistence;
using CarRental.Application.Common.Security;
using CarRental.Domain.Payments;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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
/// is processed on its own. The orders of every agency are read (one merchant account for the platform), each agency's in
/// a scope working for that agency only (ADR-0067).
/// </summary>
internal sealed partial class OnlinePaymentCatchUpJob(
    IAppDbContext db,
    IOnlinePaymentAgencyScopes scopes,
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
            .Select(p => new AgencyPayment(p.AgencyId, p.Id))
            .Take(MaxPerRun)
            .ToListAsync(cancellationToken);
        var refunded = await db.Set<Payment>()
            .AsNoTracking()
            .Where(r => r.Method == PaymentMethod.ClicToPay && r.Direction == PaymentDirection.Refund && r.Status == PaymentStatus.Pending
                        && r.PendingGatewayOperation == PendingGatewayOperation.Refund && r.PendingGatewaySinceUtc <= before
                        && r.OriginalPaymentId != null)
            .OrderByDescending(r => r.PendingGatewaySinceUtc)
            .Select(r => new AgencyPayment(r.AgencyId, r.OriginalPaymentId!.Value))
            .Take(MaxPerRun)
            .ToListAsync(cancellationToken);

        foreach (var agency in incoming.Concat(refunded).Distinct().GroupBy(p => p.AgencyId))
        {
            await scopes.RunAsync(
                agency.Key,
                async (online, ct) =>
                {
                    foreach (var payment in agency)
                    {
                        try
                        {
                            await online.CatchUpAsync(payment.PaymentId, ct);
                        }
#pragma warning disable CA1031 // One payment that cannot be processed must not stop the others; the next run retries it.
                        catch (Exception exception) when (exception is not OperationCanceledException)
#pragma warning restore CA1031
                        {
                            LogFailed(exception, payment.PaymentId);
                        }
                    }
                },
                cancellationToken);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Catch-up of online payment {PaymentId} failed; the next run retries it.")]
    private partial void LogFailed(Exception exception, Guid paymentId);
}

/// <summary>
/// Daily reconciliation of the online payments with the gateway (SPEC §9): the status of every online payment of the
/// last days, newest first, is read and compared with the recorded one (status, captured and refunded amounts when
/// reported) — including the pending ones the catch-up left aside; differences are flagged for the back office. Every
/// agency's payments, each agency's in a scope working for that agency only (ADR-0067).
/// TODO(spec): no merchant statement format of SMT is known yet, so the statement itself is not imported (settlements
/// and commissions are reconciled in Phase 3).
/// </summary>
internal sealed partial class OnlinePaymentReconciliationJob(
    IAppDbContext db,
    IOnlinePaymentAgencyScopes scopes,
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
        var payments = await db.Set<Payment>()
            .AsNoTracking()
            .Where(p => p.Method == PaymentMethod.ClicToPay && p.Direction == PaymentDirection.Incoming && p.RecordedAtUtc >= since)
            .OrderByDescending(p => p.RecordedAtUtc)
            .Select(p => new AgencyPayment(p.AgencyId, p.Id))
            .Take(BatchSize)
            .ToListAsync(cancellationToken);

        var unreadable = 0;
        foreach (var agency in payments.GroupBy(p => p.AgencyId))
        {
            await scopes.RunAsync(
                agency.Key,
                async (online, ct) =>
                {
                    foreach (var payment in agency)
                    {
                        try
                        {
                            if ((await online.ReconcileAsync(payment.PaymentId, ct)).IsFailure)
                            {
                                unreadable++;
                            }
                        }
#pragma warning disable CA1031 // One payment that cannot be compared must not stop the others.
                        catch (Exception exception) when (exception is not OperationCanceledException)
#pragma warning restore CA1031
                        {
                            unreadable++;
                            LogFailed(exception, payment.PaymentId);
                        }
                    }
                },
                cancellationToken);
        }

        LogDone(payments.Count, unreadable);
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Reconciliation of online payment {PaymentId} failed.")]
    private partial void LogFailed(Exception exception, Guid paymentId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Online payments reconciled: {Count} compared, {Unreadable} could not be read.")]
    private partial void LogDone(int count, int unreadable);
}

/// <summary>Runs the online payment flows of one agency for a job of the platform (ADR-0067).</summary>
internal interface IOnlinePaymentAgencyScopes
{
    /// <summary>Runs <paramref name="work"/> on the online payments of <paramref name="agencyId"/>, working for that agency only.</summary>
    Task RunAsync(Guid agencyId, Func<OnlinePayments, CancellationToken, Task> work, CancellationToken cancellationToken);
}

/// <summary>
/// <see cref="IOnlinePaymentAgencyScopes"/> in a DI scope of its own, as the system actor of the job (its name and
/// correlation id) working for the agency only.
/// </summary>
internal sealed class OnlinePaymentAgencyScopes(IServiceScopeFactory scopeFactory, IUserContext userContext) : IOnlinePaymentAgencyScopes
{
    public const string ActorName = "system:payments";

    public async Task RunAsync(Guid agencyId, Func<OnlinePayments, CancellationToken, Task> work, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);
        if (!userContext.IsSystem)
        {
            throw new InvalidOperationException("The online payments of an agency are processed for a job of the platform only.");
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ScopedUserContext>().Initialize(
            UserContextSnapshot.System(userContext.UserName ?? ActorName, userContext.CorrelationId, agencyId));
        await work(scope.ServiceProvider.GetRequiredService<OnlinePayments>(), cancellationToken);
    }
}

/// <summary>An online payment to process and its agency.</summary>
internal sealed record AgencyPayment(Guid AgencyId, Guid PaymentId);
