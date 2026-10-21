using Hangfire;
using Hangfire.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CarRental.Infrastructure.Outbox;

/// <summary>
/// One-permit wake-up signal of the <see cref="OutboxPump"/>. Signals coalesce: however many commits happen while the
/// pump is busy, it runs exactly once more afterwards, so no commit is ever left waiting for the scheduled run.
/// </summary>
internal sealed class OutboxWakeup : IDisposable
{
    private readonly SemaphoreSlim _signal = new(0, 1);

    public void Signal()
    {
        try
        {
            _signal.Release();
        }
        catch (SemaphoreFullException)
        {
            // A wake-up is already pending: the next run sees this commit's messages too.
        }
    }

    /// <summary>Waits for a signal or for <paramref name="timeout"/>; returns <see langword="true"/> when signaled.</summary>
    public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken) => _signal.WaitAsync(timeout, cancellationToken);

    public void Dispose() => _signal.Dispose();
}

/// <summary>Triggers the in-process <see cref="OutboxPump"/> after a commit that wrote outbox messages (ADR-0043).</summary>
internal sealed class InProcessOutboxDispatcher(OutboxWakeup wakeup) : IOutboxDispatcher
{
    public void TriggerProcessing() => wakeup.Signal();
}

/// <summary>
/// Processes the outbox right after commits, in the background of this instance: waits for a <see cref="OutboxWakeup"/>
/// signal (or <see cref="OutboxOptions.PumpIdleInterval"/> to pick up due retries), then runs the processor under the same
/// distributed lock as the every-minute Hangfire job, so that only one processor runs at a time across instances.
/// </summary>
/// <remarks>
/// Before ADR-0043, each commit enqueued a Hangfire job that waited one second for the lock and gave up when a run was in
/// progress: under load the workers were busy waiting, and a message committed after a run's last read waited for the
/// next scheduled minute.
/// </remarks>
internal sealed partial class OutboxPump(
    IServiceProvider services,
    OutboxWakeup wakeup,
    IOptions<OutboxOptions> options,
    ILogger<OutboxPump> logger) : BackgroundService
{
    /// <summary>Same lock as <c>RecurringJobRunner</c> takes for the <see cref="OutboxProcessingJob"/>.</summary>
    internal static string LockName => $"carrental:job:{OutboxProcessingJob.JobId}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await wakeup.WaitAsync(options.Value.PumpIdleInterval, stoppingToken);
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // The pump must survive any failure: the next signal or idle tick runs it again.
            catch (Exception exception)
#pragma warning restore CA1031
            {
                LogRunFailed(exception);
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
        }
    }

    private async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        // Resolved per run, not at construction: Hangfire's storage needs the database, which the migrations create at startup.
        var storage = services.GetRequiredService<JobStorage>();
        using var connection = storage.GetConnection();
        IDisposable distributedLock;
        try
        {
            distributedLock = connection.AcquireDistributedLock(LockName, options.Value.PumpLockTimeout);
        }
        catch (DistributedLockTimeoutException)
        {
            // Another processor (another instance or the scheduled job) holds the lock and will see the messages;
            // keep the wake-up so that this instance checks again right after.
            wakeup.Signal();
            return;
        }

        using (distributedLock)
        {
            await services.GetRequiredService<OutboxProcessor>().ProcessPendingAsync(cancellationToken);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Outbox pump run failed; it runs again at the next signal or idle tick.")]
    private partial void LogRunFailed(Exception exception);
}
