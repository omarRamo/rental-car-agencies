using Hangfire;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace CarRental.Infrastructure.BackgroundJobs;

/// <summary>Readiness: Hangfire storage answers; degraded when this process should run a server but none is alive.</summary>
internal sealed class HangfireHealthCheck(JobStorage storage, IOptions<BackgroundJobsOptions> options, TimeProvider timeProvider) : IHealthCheck
{
    private static readonly TimeSpan HeartbeatTolerance = TimeSpan.FromMinutes(2);

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.Run(
            () =>
            {
                var monitoring = storage.GetMonitoringApi();
                var statistics = monitoring.GetStatistics();
                var data = new Dictionary<string, object>
                {
                    ["servers"] = statistics.Servers,
                    ["enqueued"] = statistics.Enqueued,
                    ["failed"] = statistics.Failed,
                };

                if (!options.Value.RunServer)
                {
                    return HealthCheckResult.Healthy("Storage reachable (no server in this process).", data);
                }

                var threshold = timeProvider.GetUtcNow().UtcDateTime - HeartbeatTolerance;
                var alive = monitoring.Servers().Any(s => s.Heartbeat is { } heartbeat && heartbeat >= threshold);
                return alive
                    ? HealthCheckResult.Healthy("Storage reachable, server alive.", data)
                    : HealthCheckResult.Degraded("No Hangfire server heartbeat in the last two minutes.", data: data);
            },
            cancellationToken);
}
