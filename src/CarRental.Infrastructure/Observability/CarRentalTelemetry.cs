using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace CarRental.Infrastructure.Observability;

/// <summary>Activity source and meter of the platform (exported by OpenTelemetry: sources and meters named <c>CarRental*</c>).</summary>
public static class CarRentalTelemetry
{
    public const string Name = "CarRental";

    public static readonly ActivitySource ActivitySource = new(Name);

    public static readonly Meter Meter = new(Name);

    public static readonly Counter<long> OutboxMessagesProcessed =
        Meter.CreateCounter<long>("carrental.outbox.processed", description: "Outbox messages processed successfully.");

    public static readonly Counter<long> OutboxMessagesFailed =
        Meter.CreateCounter<long>("carrental.outbox.failed", description: "Outbox message processing failures.");
}
