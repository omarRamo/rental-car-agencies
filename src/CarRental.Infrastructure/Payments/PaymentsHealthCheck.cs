using System.Globalization;
using CarRental.Application.Common.Security;
using CarRental.Application.Payments.Online;
using CarRental.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace CarRental.Infrastructure.Payments;

/// <summary>
/// Readiness entry <c>payments</c> (ADR-0060): Degraded — never Unhealthy, the application keeps serving — while an
/// online payment is flagged for review (gateway confirmation missing or contradicting the records) or a capture, a
/// reversal or a refund stays unconfirmed for over an hour. Card declines are normal outcomes and never count; technical
/// failures of the calls themselves are watched through the counter <c>carrental.clictopay.calls</c>. Healthy again
/// once the payments are marked as checked in <c>/admin/payments</c> or confirmed by the catch-up. The health of the
/// platform: the payments of every agency are counted, in a scope of its own acting as the system (ADR-0067).
/// </summary>
internal sealed class PaymentsHealthCheck(IServiceScopeFactory scopes, TimeProvider timeProvider) : IHealthCheck
{
    public const string ActorName = "system:health";

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ScopedUserContext>().Initialize(UserContextSnapshot.System(ActorName));
        var db = scope.ServiceProvider.GetRequiredService<CarRentalDbContext>();
        var statistics = await OnlinePaymentMonitoring.ReadStatisticsAsync(db, now, cancellationToken);
        var data = new Dictionary<string, object>
        {
            ["flagged"] = statistics.FlaggedCount,
            ["unconfirmedOperations"] = statistics.UnconfirmedOperationCount,
            ["oldestUnconfirmedMinutes"] = statistics.OldestUnconfirmedSinceUtc is { } since ? Math.Round((now - since).TotalMinutes, 1) : 0d,
        };

        return statistics is { FlaggedCount: 0, UnconfirmedOperationCount: 0 }
            ? HealthCheckResult.Healthy("Online payments are confirmed.", data)
            : HealthCheckResult.Degraded(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{statistics.FlaggedCount} online payment(s) to review and {statistics.UnconfirmedOperationCount} gateway operation(s) unconfirmed for over an hour: see /admin/payments."),
                data: data);
    }
}
