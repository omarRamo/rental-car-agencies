using System.Globalization;
using CarRental.Application.Channels.Adapters;
using CarRental.Application.Channels.Administration;
using CarRental.Application.Channels.Outbound;
using CarRental.Application.Common.Security;
using CarRental.Infrastructure.BackgroundJobs;
using CarRental.Infrastructure.Persistence;
using Hangfire;
using Hangfire.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CarRental.Infrastructure.Channels;

/// <summary>
/// Composition of the channels (ADR-0052, ADR-0053): the Booking.com adapter (empty until its specification), the test
/// adapter in Development and Testing only, the synchronization pump of this instance (with the job server) and the
/// readiness entry <c>channels</c>.
/// </summary>
public static class ChannelsSetup
{
    /// <summary>Name of the environment of the automated tests.</summary>
    public const string TestingEnvironment = "Testing";

    public static IServiceCollection AddChannelsInfrastructure(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        services.AddOptions<ChannelSyncOptions>()
            .Bind(configuration.GetSection(ChannelSyncOptions.SectionName))
            .Validate(
                o => o.Debounce >= TimeSpan.Zero && o.MaxAttempts is >= 1 and <= 50 && o.StaleAfter > TimeSpan.Zero && o.BatchSize is >= 1 and <= 500
                     && o.ClaimLease > TimeSpan.Zero && o.PumpIdleInterval > TimeSpan.Zero,
                "Channels:Sync: Debounce >= 0, MaxAttempts 1-50, BatchSize 1-500, StaleAfter, ClaimLease and PumpIdleInterval positive.")
            .ValidateOnStart();

        services.AddSingleton<IChannelAdapter, BookingComChannelAdapter>();
        if (environment.IsDevelopment() || environment.IsEnvironment(TestingEnvironment))
        {
            // TEST-ONLY channel: never registered in recette or production.
            services.AddSingleton<FakeChannelOutbox>();
            services.AddSingleton<IChannelAdapter, FakeChannelAdapter>();
        }

        var jobs = configuration.GetSection(BackgroundJobsOptions.SectionName).Get<BackgroundJobsOptions>() ?? new BackgroundJobsOptions();
        if (jobs.RunServer)
        {
            services.AddHostedService<ChannelSyncPump>();
        }

        services.AddHealthChecks().AddCheck<ChannelsHealthCheck>("channels", tags: [HealthTags.Ready]);
        return services;
    }
}

/// <summary>
/// Sends the channel updates in the background of this instance: woken by a change (availability notified after a
/// commit, a quota, a replay), it waits the debounce window so that a burst of changes makes one update per channel and
/// category, then runs the dispatcher under the same distributed lock as the every-minute job (one run at a time,
/// across instances). Without a signal it runs every <see cref="ChannelSyncOptions.PumpIdleInterval"/> for due retries.
/// </summary>
internal sealed partial class ChannelSyncPump(
    IServiceProvider services,
    ChannelSyncSignal signal,
    IOptions<ChannelSyncOptions> options,
    ILogger<ChannelSyncPump> logger) : BackgroundService
{
    internal const string ActorName = "system:channels-sync";

    /// <summary>Same lock as <c>RecurringJobRunner</c> takes for the <see cref="ChannelSyncJob"/>.</summary>
    internal static string LockName => $"carrental:job:{ChannelSyncJob.JobId}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (await signal.WaitAsync(options.Value.PumpIdleInterval, stoppingToken) && options.Value.Debounce > TimeSpan.Zero)
                {
                    await Task.Delay(options.Value.Debounce, stoppingToken);
                }

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
        // Resolved per run: Hangfire's storage needs the database, which the migrations create at startup.
        var storage = services.GetRequiredService<JobStorage>();
        using var connection = storage.GetConnection();
        IDisposable distributedLock;
        try
        {
            distributedLock = connection.AcquireDistributedLock(LockName, TimeSpan.FromSeconds(30));
        }
        catch (DistributedLockTimeoutException)
        {
            // The job (or another instance) is running: keep the wake-up so that this instance checks again right after.
            signal.Wake();
            return;
        }

        using (distributedLock)
        {
            await using var scope = services.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<ScopedUserContext>().Initialize(UserContextSnapshot.System(ActorName));
            await scope.ServiceProvider.GetRequiredService<ChannelSyncDispatcher>().RunAsync(cancellationToken);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Channel sync pump run failed; it runs again at the next signal or idle tick.")]
    private partial void LogRunFailed(Exception exception);
}

/// <summary>
/// Readiness entry <c>channels</c>: Degraded — never Unhealthy, the application keeps serving — when an outbound update
/// failed for good (replay it from <c>/admin/channels/sync</c>) or when the oldest pending update is due for longer than
/// <see cref="ChannelSyncOptions.StaleAfter"/> (synchronization stopped, or a channel failing again and again).
/// </summary>
internal sealed class ChannelsHealthCheck(CarRentalDbContext db, IOptions<ChannelSyncOptions> options, TimeProvider timeProvider) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var statistics = await ChannelMonitoringService.ReadStatisticsAsync(db, cancellationToken);
        var overdue = statistics.OldestDueAtUtc is { } due ? timeProvider.GetUtcNow() - due : (TimeSpan?)null;
        var data = new Dictionary<string, object>
        {
            ["failed"] = statistics.FailedCount,
            ["pending"] = statistics.PendingCount,
            ["retrying"] = statistics.RetryingCount,
            ["overdueMinutes"] = overdue is { } late && late > TimeSpan.Zero ? Math.Round(late.TotalMinutes, 1) : 0d,
            ["extranetReminders"] = statistics.PendingReminders,
        };

        if (statistics.FailedCount > 0)
        {
            return HealthCheckResult.Degraded(
                string.Create(CultureInfo.InvariantCulture, $"{statistics.FailedCount} channel update(s) failed for good: see /admin/channels/sync."),
                data: data);
        }

        return overdue is { } waiting && waiting > options.Value.StaleAfter
            ? HealthCheckResult.Degraded(
                string.Create(CultureInfo.InvariantCulture, $"The oldest pending channel update is overdue by {waiting.TotalMinutes:F0} minutes."),
                data: data)
            : HealthCheckResult.Healthy("Channel updates flow.", data);
    }
}
