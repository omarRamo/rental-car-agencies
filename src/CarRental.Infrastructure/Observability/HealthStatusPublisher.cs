using System.Collections.Concurrent;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;

namespace CarRental.Infrastructure.Observability;

/// <summary>
/// Turns every health entry into telemetry (ADR-0060): the health check publisher of ASP.NET Core evaluates all the
/// registered entries every minute (<see cref="HealthTelemetry"/>) and this publisher records the status of
/// each one in the gauge <see cref="CarRentalTelemetry.HealthStatus"/> (<c>carrental.health.status</c>, tag
/// <see cref="CarRentalTelemetry.EntryTag"/>). One alert rule watches the gauge: any entry not healthy for 15 minutes
/// raises it, so an entry added later (a module, another package) is covered without a new rule.
/// </summary>
/// <remarks>
/// A change of status is also logged once (a warning when an entry stops being healthy, an information when it is
/// healthy again), with the description of the entry — counts and paths, never personal data — so that the traces show
/// when and why; a status that stays the same is not logged again.
/// </remarks>
public sealed partial class HealthStatusPublisher(ILogger<HealthStatusPublisher> logger) : IHealthCheckPublisher
{
    private readonly ConcurrentDictionary<string, HealthStatus> _last = new(StringComparer.Ordinal);

    /// <summary>The value recorded for <paramref name="status"/>: 2 healthy, 1 degraded, 0 unhealthy.</summary>
    public static int GaugeValue(HealthStatus status) => status switch
    {
        HealthStatus.Healthy => 2,
        HealthStatus.Degraded => 1,
        _ => 0,
    };

    public Task PublishAsync(HealthReport report, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(report);
        foreach (var (name, entry) in report.Entries)
        {
            CarRentalTelemetry.HealthStatus.Record(GaugeValue(entry.Status), new KeyValuePair<string, object?>(CarRentalTelemetry.EntryTag, name));
            var previous = _last.TryGetValue(name, out var known) ? known : (HealthStatus?)null;
            _last[name] = entry.Status;
            if (previous == entry.Status || (previous is null && entry.Status == HealthStatus.Healthy))
            {
                continue;
            }

            if (entry.Status == HealthStatus.Healthy)
            {
                LogRecovered(name);
            }
            else
            {
                LogNotHealthy(entry.Exception, name, entry.Status, entry.Description ?? string.Empty);
            }
        }

        return Task.CompletedTask;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Health entry {Entry} is {Status}: {Description}")]
    private partial void LogNotHealthy(Exception? exception, string entry, HealthStatus status, string description);

    [LoggerMessage(Level = LogLevel.Information, Message = "Health entry {Entry} is healthy again.")]
    private partial void LogRecovered(string entry);
}
