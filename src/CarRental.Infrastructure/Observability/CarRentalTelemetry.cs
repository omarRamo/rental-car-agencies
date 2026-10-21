using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace CarRental.Infrastructure.Observability;

/// <summary>
/// Activity source and meter of the platform (exported by OpenTelemetry: sources and meters named <c>CarRental*</c>).
/// Instruments are named <c>carrental.*</c> and carry low-cardinality tags only (an entry, a job, an operation, an
/// outcome): never an identifier, an address or any personal data. The alert rules of <c>infra/modules/alerts.bicep</c>
/// read them by name (ADR-0060).
/// </summary>
public static class CarRentalTelemetry
{
    public const string Name = "CarRental";

    /// <summary>Tag of <see cref="HealthStatus"/>: the name of the health entry (<c>sql</c>, <c>outbox</c>, <c>jobs</c>…).</summary>
    public const string EntryTag = "entry";

    /// <summary>Tag of <see cref="RecurringJobRuns"/>: the stable id of the recurring job (<c>fleet.deadline_alerts</c>…).</summary>
    public const string JobTag = "job";

    /// <summary>Tag of the counters that report how an operation ended.</summary>
    public const string OutcomeTag = "outcome";

    public static readonly ActivitySource ActivitySource = new(Name);

    public static readonly Meter Meter = new(Name);

    public static readonly Counter<long> OutboxMessagesProcessed =
        Meter.CreateCounter<long>("carrental.outbox.processed", description: "Outbox messages processed successfully.");

    public static readonly Counter<long> OutboxMessagesFailed =
        Meter.CreateCounter<long>("carrental.outbox.failed", description: "Outbox message processing failures.");

    /// <summary>
    /// Status of each health entry at its last evaluation by <see cref="HealthStatusPublisher"/>: 2 healthy, 1 degraded,
    /// 0 unhealthy (the values of <c>HealthStatus</c>), tagged with <see cref="EntryTag"/>.
    /// </summary>
    public static readonly Gauge<int> HealthStatus = Meter.CreateGauge<int>(
        "carrental.health.status",
        unit: "{status}",
        description: "Status of each health entry: 2 healthy, 1 degraded, 0 unhealthy (tag entry).");

    /// <summary>Runs of the recurring jobs, tagged with <see cref="JobTag"/> and <see cref="OutcomeTag"/> (<see cref="JobOutcomes"/>).</summary>
    public static readonly Counter<long> RecurringJobRuns = Meter.CreateCounter<long>(
        "carrental.jobs.runs",
        unit: "{run}",
        description: "Runs of the recurring jobs by job and outcome (succeeded, failed, skipped, cancelled).");

    /// <summary>Values of <see cref="OutcomeTag"/> on <see cref="RecurringJobRuns"/>.</summary>
    public static class JobOutcomes
    {
        public const string Succeeded = "succeeded";
        public const string Failed = "failed";

        /// <summary>The previous run of the same job was still running: this one did nothing.</summary>
        public const string Skipped = "skipped";

        /// <summary>The application stopped during the run.</summary>
        public const string Cancelled = "cancelled";
    }
}
