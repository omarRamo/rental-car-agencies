using System.Globalization;
using CarRental.Application.Common.BackgroundJobs;
using Hangfire;
using Hangfire.States;
using Hangfire.Storage;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace CarRental.Infrastructure.BackgroundJobs;

/// <summary>
/// Readiness entry <c>jobs</c> (ADR-0060): Degraded — never Unhealthy, the application keeps serving — when a registered
/// recurring job is not scheduled in Hangfire, cannot be scheduled (cron or type error), failed at its last run, or is
/// overdue by more than <see cref="OverdueTolerance"/> (scheduler stopped). The daily e-mail of the fleet deadlines, the
/// payment catch-up, the purges… are thus watched by the same health alert as the other entries. Degraded stays until the
/// next successful run, or until the failed run is deleted or requeued in <c>/admin/jobs</c>.
/// </summary>
/// <remarks>
/// Registered as a singleton: the evaluation (a few queries per job in the Hangfire tables) is kept
/// <see cref="CacheDuration"/>, so that the probes and the telemetry, each once a minute, cost little on Azure SQL Basic.
/// </remarks>
internal sealed class RecurringJobsHealthCheck(
    JobStorage storage,
    IEnumerable<RecurringJobDescriptor> descriptors,
    IOptions<BackgroundJobsOptions> options,
    TimeProvider timeProvider) : IHealthCheck
{
    /// <summary>A job whose next run is this much in the past is no longer triggered.</summary>
    public static readonly TimeSpan OverdueTolerance = TimeSpan.FromMinutes(15);

    /// <summary>How long an evaluation is reused.</summary>
    public static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);

    private readonly Lock _gate = new();
    private (DateTimeOffset At, HealthCheckResult Result)? _cached;

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (!options.Value.RunServer)
        {
            return HealthCheckResult.Healthy("No job server in this process.");
        }

        var now = timeProvider.GetUtcNow();
        lock (_gate)
        {
            if (_cached is { } cached && now - cached.At < CacheDuration)
            {
                return cached.Result;
            }
        }

        var ids = descriptors.Select(d => d.JobId).Distinct(StringComparer.Ordinal).ToList();
        var result = await Task.Run(
            () =>
            {
                using var connection = storage.GetConnection();
                return Evaluate(connection.GetRecurringJobs(ids), now);
            },
            cancellationToken);
        lock (_gate)
        {
            _cached = (now, result);
        }

        return result;
    }

    /// <summary>The status of the registered jobs, as read from Hangfire at <paramref name="now"/>.</summary>
    internal static HealthCheckResult Evaluate(IReadOnlyCollection<RecurringJobDto> jobs, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        var missing = Names(jobs.Where(j => j.Removed));
        var broken = Names(jobs.Where(j => !j.Removed && !string.IsNullOrEmpty(j.Error)));
        var failed = Names(jobs.Where(j => !j.Removed && string.Equals(j.LastJobState, FailedState.StateName, StringComparison.Ordinal)));
        var overdue = Names(jobs.Where(j => !j.Removed && j.NextExecution is { } next && now - AsUtc(next) > OverdueTolerance));
        var data = new Dictionary<string, object>
        {
            ["jobs"] = jobs.Count,
            ["missing"] = missing.Count,
            ["broken"] = broken.Count,
            ["failed"] = failed.Count,
            ["overdue"] = overdue.Count,
        };

        var problems = new[]
            {
                (Label: "not scheduled", Jobs: missing),
                (Label: "cannot be scheduled", Jobs: broken),
                (Label: "failed at their last run", Jobs: failed),
                (Label: "overdue", Jobs: overdue),
            }
            .Where(p => p.Jobs.Count > 0)
            .Select(p => string.Create(CultureInfo.InvariantCulture, $"{p.Label}: {string.Join(", ", p.Jobs)}"))
            .ToList();

        return problems.Count == 0
            ? HealthCheckResult.Healthy("Recurring jobs run on schedule.", data)
            : HealthCheckResult.Degraded($"Recurring jobs to check in /admin/jobs — {string.Join("; ", problems)}.", data: data);
    }

    private static List<string> Names(IEnumerable<RecurringJobDto> jobs) => [.. jobs.Select(j => j.Id).Order(StringComparer.Ordinal)];

    /// <summary>Hangfire stores UTC instants; the kind of a value read back may be unspecified.</summary>
    private static DateTimeOffset AsUtc(DateTime instant) =>
        instant.Kind == DateTimeKind.Local
            ? new DateTimeOffset(instant.ToUniversalTime(), TimeSpan.Zero)
            : new DateTimeOffset(DateTime.SpecifyKind(instant, DateTimeKind.Utc), TimeSpan.Zero);
}
