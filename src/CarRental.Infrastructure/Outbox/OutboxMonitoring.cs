using System.Globalization;
using CarRental.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace CarRental.Infrastructure.Outbox;

/// <summary>
/// The state of the outbox queue: messages that failed for good (no automatic retry left), pending messages (new or
/// waiting for a retry) and the oldest of them.
/// </summary>
public sealed record OutboxStatistics(int FailedCount, int PendingCount, int RetryingCount, DateTimeOffset? OldestPendingAtUtc)
{
    public static async Task<OutboxStatistics> ReadAsync(CarRentalDbContext db, int maxAttempts, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        var unprocessed = db.Set<OutboxMessage>().AsNoTracking().Where(m => m.ProcessedAtUtc == null);
        var failed = await unprocessed.CountAsync(m => m.AttemptCount >= maxAttempts, cancellationToken);
        var pending = await unprocessed
            .Where(m => m.AttemptCount < maxAttempts)
            .GroupBy(_ => 1)
            .Select(g => new { Count = g.Count(), Retrying = g.Count(m => m.AttemptCount > 0), Oldest = g.Min(m => m.OccurredAtUtc) })
            .FirstOrDefaultAsync(cancellationToken);
        return new OutboxStatistics(failed, pending?.Count ?? 0, pending?.Retrying ?? 0, pending?.Oldest);
    }
}

/// <summary>
/// Readiness entry <c>outbox</c>: Degraded — never Unhealthy, the application keeps serving — when messages failed for
/// good or when the oldest pending message waits longer than <see cref="OutboxOptions.PendingAlertAge"/> (processing
/// stopped, or a message failing again and again).
/// </summary>
internal sealed class OutboxHealthCheck(CarRentalDbContext db, IOptions<OutboxOptions> options, TimeProvider timeProvider) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var settings = options.Value;
        var statistics = await OutboxStatistics.ReadAsync(db, settings.MaxAttempts, cancellationToken);
        var oldestAge = statistics.OldestPendingAtUtc is { } oldest ? timeProvider.GetUtcNow() - oldest : (TimeSpan?)null;
        var data = new Dictionary<string, object>
        {
            ["failed"] = statistics.FailedCount,
            ["pending"] = statistics.PendingCount,
            ["retrying"] = statistics.RetryingCount,
            ["oldestPendingMinutes"] = oldestAge is { } age ? Math.Round(age.TotalMinutes, 1) : 0d,
        };

        if (statistics.FailedCount > 0)
        {
            return HealthCheckResult.Degraded(
                string.Create(CultureInfo.InvariantCulture, $"{statistics.FailedCount} outbox message(s) failed for good: see /admin/outbox."),
                data: data);
        }

        return oldestAge is { } waiting && waiting > settings.PendingAlertAge
            ? HealthCheckResult.Degraded(
                string.Create(CultureInfo.InvariantCulture, $"The oldest pending outbox message waits for {waiting.TotalMinutes:F0} minutes."),
                data: data)
            : HealthCheckResult.Healthy("Outbox messages flow.", data);
    }
}
