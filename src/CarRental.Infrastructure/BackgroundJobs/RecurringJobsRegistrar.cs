using CarRental.Application.Common.BackgroundJobs;
using CarRental.Domain.Common;
using Hangfire;
using Hangfire.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CarRental.Infrastructure.BackgroundJobs;

/// <summary>Registers every <see cref="RecurringJobDescriptor"/> with Hangfire at startup (cron in Africa/Tunis) and removes stale ones.</summary>
internal sealed partial class RecurringJobsRegistrar(
    IEnumerable<RecurringJobDescriptor> descriptors,
    IRecurringJobManager recurringJobs,
    JobStorage storage,
    ILogger<RecurringJobsRegistrar> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var known = new HashSet<string>(StringComparer.Ordinal);
            foreach (var descriptor in descriptors)
            {
                var jobId = descriptor.JobId;
                known.Add(jobId);
                recurringJobs.AddOrUpdate<RecurringJobRunner>(
                    jobId,
                    runner => runner.RunAsync(jobId, CancellationToken.None),
                    descriptor.CronExpression,
                    new RecurringJobOptions { TimeZone = TunisTime.Zone });
            }

            using var connection = storage.GetConnection();
            foreach (var stale in connection.GetRecurringJobs().Where(j => !known.Contains(j.Id)))
            {
                recurringJobs.RemoveIfExists(stale.Id);
                LogRemoved(stale.Id);
            }
        }
#pragma warning disable CA1031 // Startup must not fail because the job storage is temporarily unavailable.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            LogRegistrationFailed(exception);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Information, Message = "Stale recurring job {JobId} removed.")]
    private partial void LogRemoved(string jobId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Recurring jobs could not be registered.")]
    private partial void LogRegistrationFailed(Exception exception);
}
