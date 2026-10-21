using System.Diagnostics;
using CarRental.Application.Common.BackgroundJobs;
using CarRental.Application.Common.Security;
using CarRental.Infrastructure.Observability;
using Hangfire;
using Hangfire.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CarRental.Infrastructure.BackgroundJobs;

/// <summary>
/// The single Hangfire entry point for <see cref="IRecurringJob"/> implementations: resolves the job by id in the job's
/// DI scope, runs it as the system actor, and never lets two runs of the same job overlap (distributed lock; a run that
/// cannot get it is skipped). Jobs handle their own retries: Hangfire retries are disabled. Each run of a registered job
/// is counted in <see cref="CarRentalTelemetry.RecurringJobRuns"/> with its outcome.
/// </summary>
public sealed partial class RecurringJobRunner(
    IServiceProvider services,
    IEnumerable<RecurringJobDescriptor> descriptors,
    JobStorage storage,
    ILogger<RecurringJobRunner> logger)
{
    [AutomaticRetry(Attempts = 0)]
    [JobDisplayName("{0}")]
    public async Task RunAsync(string jobId, CancellationToken cancellationToken)
    {
        var descriptor = descriptors.FirstOrDefault(d => string.Equals(d.JobId, jobId, StringComparison.Ordinal));
        if (descriptor is null)
        {
            LogUnknownJob(jobId);
            return;
        }

        using var connection = storage.GetConnection();
        IDisposable jobLock;
        try
        {
            jobLock = connection.AcquireDistributedLock($"carrental:job:{jobId}", TimeSpan.FromSeconds(1));
        }
        catch (DistributedLockTimeoutException)
        {
            LogSkipped(jobId);
            Count(descriptor, CarRentalTelemetry.JobOutcomes.Skipped);
            return;
        }

        using (jobLock)
        {
            using var activity = CarRentalTelemetry.ActivitySource.StartActivity($"job {jobId}", ActivityKind.Internal);
            services.GetRequiredService<ScopedUserContext>()
                .Initialize(UserContextSnapshot.System($"system:job:{jobId}", Activity.Current?.TraceId.ToString()));
            var job = (IRecurringJob)services.GetRequiredService(descriptor.JobType);
            var outcome = CarRentalTelemetry.JobOutcomes.Failed;
            try
            {
                await job.ExecuteAsync(cancellationToken);
                outcome = CarRentalTelemetry.JobOutcomes.Succeeded;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                outcome = CarRentalTelemetry.JobOutcomes.Cancelled;
                throw;
            }
            finally
            {
                Count(descriptor, outcome);
            }
        }
    }

    /// <summary>The id comes from the registered descriptor, never from storage: the tag values stay a known, short list.</summary>
    private static void Count(RecurringJobDescriptor descriptor, string outcome) =>
        CarRentalTelemetry.RecurringJobRuns.Add(
            1,
            new KeyValuePair<string, object?>(CarRentalTelemetry.JobTag, descriptor.JobId),
            new KeyValuePair<string, object?>(CarRentalTelemetry.OutcomeTag, outcome));

    [LoggerMessage(Level = LogLevel.Warning, Message = "Recurring job {JobId} is not registered; run ignored.")]
    private partial void LogUnknownJob(string jobId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Recurring job {JobId} is already running; this run is skipped.")]
    private partial void LogSkipped(string jobId);
}
