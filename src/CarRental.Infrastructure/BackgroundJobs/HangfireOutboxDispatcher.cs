using CarRental.Infrastructure.Outbox;
using Hangfire;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CarRental.Infrastructure.BackgroundJobs;

/// <summary>Enqueues an immediate outbox run; the every-minute schedule catches up if Hangfire is unavailable.</summary>
/// <remarks>
/// The Hangfire client is resolved on first use, not at construction: this dispatcher is a dependency of the DbContext
/// interceptors, and creating Hangfire's storage before the migrations have created the database would make it fail.
/// </remarks>
internal sealed partial class HangfireOutboxDispatcher(IServiceProvider services, ILogger<HangfireOutboxDispatcher> logger) : IOutboxDispatcher
{
    public void TriggerProcessing()
    {
        try
        {
            services.GetRequiredService<IBackgroundJobClient>()
                .Enqueue<RecurringJobRunner>(runner => runner.RunAsync(OutboxProcessingJob.JobId, CancellationToken.None));
        }
#pragma warning disable CA1031 // Best effort: the committed messages are processed by the next scheduled run.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            LogEnqueueFailed(exception);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Immediate outbox processing could not be enqueued; the scheduled run will process the messages.")]
    private partial void LogEnqueueFailed(Exception exception);
}
