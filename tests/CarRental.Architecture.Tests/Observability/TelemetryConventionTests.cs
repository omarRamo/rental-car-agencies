using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using CarRental.Architecture.Tests.Security;
using CarRental.Infrastructure.BackgroundJobs;
using CarRental.Infrastructure.Observability;
using CarRental.Infrastructure.Payments.ClicToPay;

namespace CarRental.Architecture.Tests.Observability;

/// <summary>
/// Conventions of the telemetry read by the alert rules (ADR-0060, <c>infra/modules/alerts.bicep</c>): instruments named
/// <c>carrental.*</c>, and run counters of the recurring jobs tagged with the registered job id and a known outcome.
/// </summary>
public sealed partial class TelemetryConventionTests
{
    [Fact]
    public void Every_instrument_of_the_platform_meter_is_named_carrental_dot_something()
    {
        // Instruments declared outside CarRentalTelemetry are created by the static initializer of their type.
        RuntimeHelpers.RunClassConstructor(typeof(CarRentalTelemetry).TypeHandle);
        RuntimeHelpers.RunClassConstructor(typeof(ClicToPayClient).TypeHandle);
        using var recorder = new TelemetryRecorder();

        recorder.Instruments.Keys.ShouldNotBeEmpty();
        recorder.Instruments.Keys.ShouldAllBe(name => InstrumentName().IsMatch(name));
        recorder.Instruments.Keys.ShouldContain("carrental.health.status");
        recorder.Instruments.Keys.ShouldContain("carrental.jobs.runs");
        recorder.Instruments.Keys.ShouldContain("carrental.clictopay.calls");
    }

    [Fact]
    public void The_names_read_by_the_alert_rules_are_the_names_of_the_instruments()
    {
        var alerts = File.ReadAllText(RepositoryFiles.PathOf("infra", "modules", "alerts.bicep"));

        alerts.ShouldContain($"name == \"{CarRentalTelemetry.HealthStatus.Name}\"");
        alerts.ShouldContain($"customDimensions[\"{CarRentalTelemetry.EntryTag}\"]");
        alerts.ShouldContain("name == \"carrental.clictopay.calls\"");
        alerts.ShouldContain("customDimensions[\"outcome\"]");
    }

    [Fact]
    public void The_overdue_tolerance_of_the_jobs_entry_stays_shorter_than_the_alert_window_plus_a_run()
    {
        // A job every minute that stops is reported within the 15-minute window of the health alert.
        RecurringJobsHealthCheck.OverdueTolerance.ShouldBeLessThanOrEqualTo(TimeSpan.FromMinutes(15));
        RecurringJobsHealthCheck.CacheDuration.ShouldBeLessThan(TimeSpan.FromMinutes(15));
    }

    [GeneratedRegex("^carrental(\\.[a-z][a-z0-9_]*)+$", RegexOptions.CultureInvariant)]
    private static partial Regex InstrumentName();
}
