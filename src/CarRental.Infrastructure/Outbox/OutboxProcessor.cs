using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using CarRental.Application.Common.BackgroundJobs;
using CarRental.Application.Common.Outbox;
using CarRental.Application.Common.Security;
using CarRental.Infrastructure.Observability;
using CarRental.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CarRental.Infrastructure.Outbox;

public sealed class OutboxOptions
{
    public const string SectionName = "Outbox";

    /// <summary>Messages loaded per batch.</summary>
    public int BatchSize { get; set; } = 50;

    /// <summary>Batches per run (a run stops earlier when nothing is due).</summary>
    public int MaxBatchesPerRun { get; set; } = 20;

    /// <summary>After this many failures a message is no longer retried (failed: listed on <c>/admin/outbox</c> until retried).</summary>
    public int MaxAttempts { get; set; } = 10;

    /// <summary>The readiness check turns Degraded when the oldest pending message is older than this (30 minutes).</summary>
    public TimeSpan PendingAlertAge { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>Without a commit signal, the in-process pump still runs this often, for due retries (15 seconds).</summary>
    public TimeSpan PumpIdleInterval { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>How long the pump waits for another processor (scheduled job, other instance) to release the lock (30 seconds).</summary>
    public TimeSpan PumpLockTimeout { get; set; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// Dispatches due outbox messages to their <see cref="IOutboxHandler{TMessage}"/> implementations, one DI scope per
/// message with the system actor <c>system:outbox</c>. A failure increments the attempt count and schedules a retry
/// with exponential back-off (1, 2, 4… minutes, at most 6 hours).
/// </summary>
public sealed partial class OutboxProcessor(
    IServiceScopeFactory scopeFactory,
    OutboxMessageTypeRegistry registry,
    OutboxSerializer serializer,
    TimeProvider timeProvider,
    IOptions<OutboxOptions> options,
    ILogger<OutboxProcessor> logger)
{
    private static readonly MethodInfo InvokeHandlersMethod =
        typeof(OutboxProcessor).GetMethod(nameof(InvokeHandlersAsync), BindingFlags.NonPublic | BindingFlags.Static)!;

    private static readonly ConcurrentDictionary<Type, Func<IServiceProvider, object, OutboxMessageContext, CancellationToken, Task>> Invokers = new();

    /// <summary>Processes due messages until none is left or the per-run limit is reached; returns the number handled.</summary>
    public async Task<int> ProcessPendingAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var handled = await CompleteMessagesWithoutHandlerAsync(cancellationToken);
        for (var batch = 0; batch < settings.MaxBatchesPerRun; batch++)
        {
            var dueIds = await LoadDueIdsAsync(settings, cancellationToken);
            if (dueIds.Count == 0)
            {
                break;
            }

            foreach (var id in dueIds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await ProcessAsync(id, cancellationToken);
                handled++;
            }
        }

        return handled;
    }

    internal static TimeSpan RetryDelay(int failedAttempts) =>
        TimeSpan.FromMinutes(Math.Min(Math.Pow(2, Math.Max(0, failedAttempts - 1)), 360));

    private static async Task InvokeHandlersAsync<TMessage>(
        IServiceProvider services,
        object message,
        OutboxMessageContext context,
        CancellationToken cancellationToken)
        where TMessage : class
    {
        foreach (var handler in services.GetServices<IOutboxHandler<TMessage>>())
        {
            await handler.HandleAsync((TMessage)message, context, cancellationToken);
        }
    }

    private static Func<IServiceProvider, object, OutboxMessageContext, CancellationToken, Task> InvokerFor(Type messageType) =>
        Invokers.GetOrAdd(
            messageType,
            type => InvokeHandlersMethod.MakeGenericMethod(type)
                .CreateDelegate<Func<IServiceProvider, object, OutboxMessageContext, CancellationToken, Task>>());

    /// <summary>
    /// Marks processed, in one statement, the pending messages never attempted whose type has no handler: they stay in the
    /// table as a record of the event (purged like the others) but have nothing to run, and walking them one by one would delay
    /// the messages that do have handlers (ADR-0040). A message that already failed stays visible on the outbox screen until a
    /// manager retries it (the retry resets its attempts).
    /// </summary>
    private async Task<int> CompleteMessagesWithoutHandlerAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CarRentalDbContext>();
        var handledTypes = registry.HandledTypeNames.ToList();
        var now = timeProvider.GetUtcNow();
        return await db.Set<OutboxMessage>()
            .Where(m => m.ProcessedAtUtc == null && m.AttemptCount == 0 && !handledTypes.Contains(m.Type))
            .ExecuteUpdateAsync(setters => setters.SetProperty(m => m.ProcessedAtUtc, now), cancellationToken);
    }

    private async Task<List<Guid>> LoadDueIdsAsync(OutboxOptions settings, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CarRentalDbContext>();
        var now = timeProvider.GetUtcNow();
        var handledTypes = registry.HandledTypeNames.ToList();
        return await db.Set<OutboxMessage>()
            .AsNoTracking()
            .Where(m => m.ProcessedAtUtc == null
                        && handledTypes.Contains(m.Type)
                        && m.AttemptCount < settings.MaxAttempts
                        && (m.NextAttemptAtUtc == null || m.NextAttemptAtUtc <= now))
            .OrderBy(m => m.OccurredAtUtc)
            .Select(m => m.Id)
            .Take(settings.BatchSize)
            .ToListAsync(cancellationToken);
    }

    private async Task ProcessAsync(Guid messageId, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CarRentalDbContext>();
        var message = await db.Set<OutboxMessage>().FirstOrDefaultAsync(m => m.Id == messageId && m.ProcessedAtUtc == null, cancellationToken);
        if (message is null)
        {
            return;
        }

        scope.ServiceProvider.GetRequiredService<ScopedUserContext>()
            .Initialize(UserContextSnapshot.System("system:outbox", message.CorrelationId));

        using var activity = CarRentalTelemetry.ActivitySource.StartActivity("outbox.process", ActivityKind.Consumer);
        activity?.SetTag("carrental.outbox.message_id", message.Id);
        activity?.SetTag("carrental.outbox.message_type", message.Type);
        activity?.SetTag("carrental.outbox.attempt", message.AttemptCount + 1);

        try
        {
            if (registry.Resolve(message.Type) is { } messageType)
            {
                var payload = serializer.Deserialize(message.Payload, messageType);
                var context = new OutboxMessageContext(message.Id, message.OccurredAtUtc, message.AttemptCount + 1, message.CorrelationId);
                await InvokerFor(messageType)(scope.ServiceProvider, payload, context, cancellationToken);
            }
            else
            {
                LogNoHandler(message.Type, message.Id);
            }

            message.MarkProcessed(timeProvider.GetUtcNow());
            await db.SaveChangesAsync(cancellationToken);
            CarRentalTelemetry.OutboxMessagesProcessed.Add(1);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            activity?.SetStatus(ActivityStatusCode.Error, exception.Message);
            CarRentalTelemetry.OutboxMessagesFailed.Add(1);
            LogFailure(exception, message.Type, message.Id, message.AttemptCount + 1);
            await RecordFailureAsync(messageId, exception, cancellationToken);
        }
    }

    /// <summary>Records the failure in a fresh context: changes left by the failing handler are discarded.</summary>
    private async Task RecordFailureAsync(Guid messageId, Exception exception, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CarRentalDbContext>();
        var now = timeProvider.GetUtcNow();
        var attempts = await db.Set<OutboxMessage>().Where(m => m.Id == messageId).Select(m => m.AttemptCount).FirstAsync(cancellationToken) + 1;
        var error = exception.ToString();
        if (error.Length > OutboxMessage.ErrorMaxLength)
        {
            error = error[..OutboxMessage.ErrorMaxLength];
        }

        var nextAttempt = now + RetryDelay(attempts);
        await db.Set<OutboxMessage>()
            .Where(m => m.Id == messageId)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(m => m.AttemptCount, attempts)
                    .SetProperty(m => m.LastError, error)
                    .SetProperty(m => m.NextAttemptAtUtc, nextAttempt),
                cancellationToken);
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "No handler for outbox message type {MessageType} ({MessageId}); marked as processed.")]
    private partial void LogNoHandler(string messageType, Guid messageId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Outbox message {MessageType} ({MessageId}) failed on attempt {Attempt}.")]
    private partial void LogFailure(Exception exception, string messageType, Guid messageId, int attempt);
}

/// <summary>Every minute, as a safety net: commits wake the in-process <see cref="OutboxPump"/> (ADR-0043).</summary>
public sealed class OutboxProcessingJob(OutboxProcessor processor) : IRecurringJob
{
    public static string JobId => "outbox.process";

    public static string CronExpression => "* * * * *";

    public Task ExecuteAsync(CancellationToken cancellationToken) => processor.ProcessPendingAsync(cancellationToken);
}
