using CarRental.Infrastructure.Observability;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;

namespace CarRental.Architecture.Tests.Observability;

/// <summary>
/// The health entries become the gauge <c>carrental.health.status</c> (ADR-0060): one measurement per entry and per
/// evaluation, tagged with the entry name, so that the alert rule covers any entry without knowing it.
/// </summary>
public sealed class HealthStatusPublisherTests
{
    private const string Gauge = "carrental.health.status";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Every_entry_is_recorded_with_its_status_and_its_name()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var report = Report(
            ($"healthy-{suffix}", HealthStatus.Healthy),
            ($"degraded-{suffix}", HealthStatus.Degraded),
            ($"unhealthy-{suffix}", HealthStatus.Unhealthy));
        using var recorder = new TelemetryRecorder();

        await new HealthStatusPublisher(new RecordingLogger<HealthStatusPublisher>()).PublishAsync(report, Ct);

        var recorded = recorder.Measurements(Gauge)
            .Where(m => m.Tags.GetValueOrDefault(CarRentalTelemetry.EntryTag)?.EndsWith(suffix, StringComparison.Ordinal) == true)
            .ToDictionary(m => m.Tags[CarRentalTelemetry.EntryTag]!, m => m.Value);
        recorded.ShouldBe(new Dictionary<string, long>
        {
            [$"healthy-{suffix}"] = 2,
            [$"degraded-{suffix}"] = 1,
            [$"unhealthy-{suffix}"] = 0,
        });
    }

    [Fact]
    public async Task Each_evaluation_records_again_so_a_lasting_problem_stays_visible()
    {
        var entry = $"outbox-{Guid.NewGuid():N}";
        var publisher = new HealthStatusPublisher(new RecordingLogger<HealthStatusPublisher>());
        using var recorder = new TelemetryRecorder();

        await publisher.PublishAsync(Report((entry, HealthStatus.Degraded)), Ct);
        await publisher.PublishAsync(Report((entry, HealthStatus.Degraded)), Ct);
        await publisher.PublishAsync(Report((entry, HealthStatus.Healthy)), Ct);

        recorder.Measurements(Gauge)
            .Where(m => m.Tags.GetValueOrDefault(CarRentalTelemetry.EntryTag) == entry)
            .Select(m => m.Value)
            .ShouldBe([1, 1, 2]);
    }

    [Fact]
    public async Task A_change_of_status_is_logged_once_with_the_description()
    {
        var entry = $"channels-{Guid.NewGuid():N}";
        var logger = new RecordingLogger<HealthStatusPublisher>();
        var publisher = new HealthStatusPublisher(logger);

        await publisher.PublishAsync(Report((entry, HealthStatus.Healthy)), Ct);
        await publisher.PublishAsync(Report((entry, HealthStatus.Degraded)), Ct);
        await publisher.PublishAsync(Report((entry, HealthStatus.Degraded)), Ct);
        await publisher.PublishAsync(Report((entry, HealthStatus.Healthy)), Ct);

        logger.Entries.Select(e => e.Level).ShouldBe([LogLevel.Warning, LogLevel.Information]);
        logger.Entries[0].Message.ShouldContain(entry);
        logger.Entries[0].Message.ShouldContain("Degraded");
        logger.Entries[0].Message.ShouldContain($"description of {entry}");
    }

    [Fact]
    public async Task An_entry_failing_from_the_first_evaluation_is_logged_with_its_exception()
    {
        var entry = $"sql-{Guid.NewGuid():N}";
        var logger = new RecordingLogger<HealthStatusPublisher>();
        var failure = new InvalidOperationException("database unreachable");
        var report = new HealthReport(
            new Dictionary<string, HealthReportEntry>
            {
                [entry] = new(HealthStatus.Unhealthy, "cannot connect", TimeSpan.FromSeconds(1), failure, null),
            },
            TimeSpan.FromSeconds(1));

        await new HealthStatusPublisher(logger).PublishAsync(report, Ct);

        logger.Entries.ShouldHaveSingleItem().Exception.ShouldBeSameAs(failure);
    }

    [Theory]
    [InlineData(HealthStatus.Healthy)]
    [InlineData(HealthStatus.Degraded)]
    [InlineData(HealthStatus.Unhealthy)]
    public void The_gauge_value_is_the_value_of_the_status(HealthStatus status)
    {
        HealthStatusPublisher.GaugeValue(status).ShouldBe((int)status);
    }

    [Fact]
    public void The_publisher_evaluates_every_entry_each_minute()
    {
        var options = new HealthCheckPublisherOptions { Predicate = _ => false };

        HealthTelemetry.Configure(options);

        options.Predicate.ShouldBeNull("entries of any tag, added by any module, must be published");
        options.Period.ShouldBe(TimeSpan.FromMinutes(1));
        options.Delay.ShouldBe(TimeSpan.FromSeconds(30));
    }

    private static HealthReport Report(params (string Name, HealthStatus Status)[] entries) =>
        new(
            entries.ToDictionary(
                e => e.Name,
                e => new HealthReportEntry(e.Status, $"description of {e.Name}", TimeSpan.FromMilliseconds(5), null, null)),
            TimeSpan.FromMilliseconds(5));
}
