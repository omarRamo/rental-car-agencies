using CarRental.Infrastructure.BackgroundJobs;
using Hangfire.States;
using Hangfire.Storage;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace CarRental.Architecture.Tests.Observability;

/// <summary>
/// The health entry <c>jobs</c> (ADR-0060): a recurring job that is missing, cannot be scheduled, failed at its last run
/// or stopped being triggered turns the entry Degraded — the fleet deadline e-mail of every morning included.
/// </summary>
public sealed class RecurringJobsHealthTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 6, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Jobs_that_ran_and_are_scheduled_are_healthy()
    {
        var result = RecurringJobsHealthCheck.Evaluate([Job("outbox.process"), Job("fleet.deadline_alerts")], Now);

        result.Status.ShouldBe(HealthStatus.Healthy);
        result.Data["jobs"].ShouldBe(2);
    }

    [Fact]
    public void A_failed_last_run_is_degraded_and_named()
    {
        var result = RecurringJobsHealthCheck.Evaluate([Job("outbox.process"), Job("fleet.deadline_alerts", lastState: FailedState.StateName)], Now);

        result.Status.ShouldBe(HealthStatus.Degraded);
        result.Description.ShouldNotBeNull().ShouldContain("failed at their last run: fleet.deadline_alerts");
        result.Description.ShouldNotContain("outbox.process");
        result.Data["failed"].ShouldBe(1);
    }

    [Fact]
    public void A_job_no_longer_triggered_is_degraded_after_the_tolerance_only()
    {
        var late = Now - RecurringJobsHealthCheck.OverdueTolerance - TimeSpan.FromMinutes(1);
        var almost = Now - RecurringJobsHealthCheck.OverdueTolerance + TimeSpan.FromMinutes(1);

        RecurringJobsHealthCheck.Evaluate([Job("channels.sync", next: late)], Now).Status.ShouldBe(HealthStatus.Degraded);
        RecurringJobsHealthCheck.Evaluate([Job("channels.sync", next: almost)], Now).Status.ShouldBe(HealthStatus.Healthy);
    }

    [Fact]
    public void A_job_missing_from_the_scheduler_or_impossible_to_schedule_is_degraded()
    {
        var missing = new RecurringJobDto { Id = "payments.online_catch_up", Removed = true };
        var broken = Job("reporting.daily_snapshot");
        broken.Error = "Could not load the job type.";

        var result = RecurringJobsHealthCheck.Evaluate([missing, broken], Now);

        result.Status.ShouldBe(HealthStatus.Degraded);
        result.Description.ShouldNotBeNull().ShouldContain("not scheduled: payments.online_catch_up");
        result.Description.ShouldContain("cannot be scheduled: reporting.daily_snapshot");
    }

    [Fact]
    public void An_unspecified_instant_read_from_storage_is_taken_as_utc()
    {
        var late = (Now - TimeSpan.FromHours(2)).UtcDateTime;
        var job = Job("bookings.expire");
        job.NextExecution = DateTime.SpecifyKind(late, DateTimeKind.Unspecified);

        RecurringJobsHealthCheck.Evaluate([job], Now).Status.ShouldBe(HealthStatus.Degraded);
    }

    private static RecurringJobDto Job(string id, string? lastState = null, DateTimeOffset? next = null) => new()
    {
        Id = id,
        Cron = "* * * * *",
        LastJobId = "1",
        LastJobState = lastState ?? SucceededState.StateName,
        LastExecution = (Now - TimeSpan.FromMinutes(1)).UtcDateTime,
        NextExecution = (next ?? Now + TimeSpan.FromMinutes(1)).UtcDateTime,
    };
}
