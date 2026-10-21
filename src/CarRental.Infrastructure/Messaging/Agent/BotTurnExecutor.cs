using System.Collections.Concurrent;
using System.Data;
using System.Globalization;
using CarRental.Application.Common.Caching;
using CarRental.Application.Common.Security;
using CarRental.Application.Messaging.Agent.Turns;
using CarRental.Domain.Messaging;
using CarRental.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CarRental.Infrastructure.Messaging.Agent;

/// <summary>
/// One-permit wake-up signal of the <see cref="BotTurnPump"/> (the pattern of the outbox pump, ADR-0043): signals
/// coalesce, and one received during a sweep makes exactly one more sweep.
/// </summary>
internal sealed class BotTurnWakeup : IDisposable
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
            // A wake-up is already pending.
        }
    }

    public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken) => _signal.WaitAsync(timeout, cancellationToken);

    public void Dispose() => _signal.Dispose();
}

/// <summary>The outbox handler of a customer message only wakes the executor: the turn runs in the background (ADR-0054).</summary>
internal sealed class BackgroundBotTurnDispatcher(BotTurnWakeup wakeup) : IBotTurnDispatcher
{
    public Task DispatchAsync(Guid conversationId, Guid messageId, string? correlationId, CancellationToken cancellationToken)
    {
        wakeup.Signal();
        return Task.CompletedTask;
    }
}

/// <summary>
/// Wakes the executor right after a commit that wrote messages (in-process, ADR-0044 signal): a customer message is
/// answered without waiting for the outbox.
/// </summary>
internal sealed class BotTurnCommitListener(BotTurnWakeup wakeup) : ICommittedChangesListener
{
    public void OnCommitted(IReadOnlySet<Type> changedEntityTypes)
    {
        if (changedEntityTypes.Contains(typeof(Message)))
        {
            wakeup.Signal();
        }
    }
}

/// <summary>
/// The bot turn executor (ADR-0054): a background service that runs the pending bot turns outside the outbox — up to
/// <see cref="BotTurnOptions.MaxParallelism"/> conversations at once, never two turns of one conversation (SQL Server
/// application lock), each once its customer has been quiet for <see cref="BotTurnOptions.QuietPeriod"/>. Woken right
/// after the commits that write messages and by the outbox handler of each customer message; swept every
/// <see cref="BotTurnOptions.SweepInterval"/> (or at the next due time) as a safety net: restarts, other instances,
/// retries. Its queue is the messages themselves, so a restart loses nothing.
/// </summary>
internal sealed partial class BotTurnPump(
    IServiceScopeFactory scopeFactory,
    IBotTurnRunner runner,
    BotTurnWakeup wakeup,
    IOptions<BotTurnOptions> options,
    TimeProvider clock,
    ILogger<BotTurnPump> logger) : BackgroundService
{
    private const string ActorName = "system:whatsapp";

    /// <summary>A conversation locked by another instance, or whose run failed, is left alone this long (no busy loop).</summary>
    private static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(2);

    private readonly ConcurrentDictionary<Guid, Task> _running = new();
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _resting = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        using var slots = new SemaphoreSlim(Math.Max(1, settings.MaxParallelism));
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await wakeup.WaitAsync(await NextWaitAsync(settings, stoppingToken), stoppingToken);
                    await DispatchDueAsync(settings, slots, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
#pragma warning disable CA1031 // The executor must survive any failure (database unavailable): the next signal or sweep runs it again.
                catch (Exception exception)
#pragma warning restore CA1031
                {
                    LogSweepFailed(exception);
                    await Task.Delay(TimeSpan.FromSeconds(1), clock, stoppingToken).ContinueWith(_ => { }, TaskScheduler.Default);
                }
            }
        }
        finally
        {
            // Turns in progress end with the shutdown token: a turn cut before its reply stays pending and runs after the restart.
            await Task.WhenAll(_running.Values).ContinueWith(_ => { }, TaskScheduler.Default);
        }
    }

    /// <summary>Until the next pending turn is due, the sweep interval at most, 50 ms at least.</summary>
    private async Task<TimeSpan> NextWaitAsync(BotTurnOptions settings, CancellationToken cancellationToken)
    {
        await using var scope = CreateScope();
        var next = await scope.ServiceProvider.GetRequiredService<IBotTurnQueue>().NextDueAtAsync(settings, cancellationToken);
        if (next is not { } due)
        {
            return settings.SweepInterval;
        }

        var wait = due - clock.GetUtcNow();
        return wait < TimeSpan.FromMilliseconds(50) ? TimeSpan.FromMilliseconds(50) : (wait > settings.SweepInterval ? settings.SweepInterval : wait);
    }

    private async Task DispatchDueAsync(BotTurnOptions settings, SemaphoreSlim slots, CancellationToken cancellationToken)
    {
        while (slots.CurrentCount > 0)
        {
            var now = clock.GetUtcNow();
            foreach (var (id, until) in _resting)
            {
                if (until <= now)
                {
                    _resting.TryRemove(id, out _);
                }
            }

            IReadOnlyList<Guid> due;
            await using (var scope = CreateScope())
            {
                var excluded = _running.Keys.Concat(_resting.Keys).ToList();
                due = await scope.ServiceProvider.GetRequiredService<IBotTurnQueue>().DueAsync(settings, excluded, slots.CurrentCount, cancellationToken);
            }

            if (due.Count == 0)
            {
                return;
            }

            foreach (var conversationId in due)
            {
                if (!await slots.WaitAsync(0, cancellationToken))
                {
                    return;
                }

                _running[conversationId] = RunAsync(conversationId, slots, cancellationToken);
            }
        }
    }

    private async Task RunAsync(Guid conversationId, SemaphoreSlim slots, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Yield();
            var outcome = await runner.RunAsync(conversationId, cancellationToken);
            if (outcome == BotTurnRunOutcome.Busy)
            {
                _resting[conversationId] = clock.GetUtcNow() + Cooldown;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutdown: the turn stays pending.
        }
#pragma warning disable CA1031 // One failed turn (database unavailable) must not stop the others; it is swept again after the cooldown.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            _resting[conversationId] = clock.GetUtcNow() + Cooldown;
            LogTurnFailed(exception.GetType().Name);
        }
        finally
        {
            _running.TryRemove(conversationId, out _);
            slots.Release();
            wakeup.Signal();
        }
    }

    private AsyncServiceScope CreateScope()
    {
        var scope = scopeFactory.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ScopedUserContext>().Initialize(UserContextSnapshot.System(ActorName));
        return scope;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "WhatsApp bot turn executor: sweep failed; it runs again at the next signal or sweep.")]
    private partial void LogSweepFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "WhatsApp bot turn executor: a turn could not run ({ExceptionType}); swept again later.")]
    private partial void LogTurnFailed(string exceptionType);
}

/// <summary>
/// Serializes the bot turns of a conversation on every instance: <c>sp_getapplock</c> on <c>conversation:{id}</c>,
/// exclusive, owned by the session of a connection of its own held for the whole turn (model calls included), taken
/// without waiting; released explicitly (and by SQL Server if the connection or the process dies).
/// </summary>
internal sealed class SqlConversationTurnLock(IOptions<DatabaseOptions> database) : IConversationTurnLock
{
    private const string AcquireSql = """
        DECLARE @result int;
        EXEC @result = sp_getapplock @Resource = @resource, @LockMode = 'Exclusive', @LockOwner = 'Session', @LockTimeout = 0;
        SELECT @result;
        """;

    private const string ReleaseSql = "EXEC sp_releaseapplock @Resource = @resource, @LockOwner = 'Session';";

    public static string ResourceFor(Guid conversationId) => string.Create(CultureInfo.InvariantCulture, $"conversation:{conversationId:D}");

    public async Task<IAsyncDisposable?> TryAcquireAsync(Guid conversationId, CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(database.Value.ConnectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = AcquireSql;
            command.Parameters.Add(new SqlParameter("@resource", SqlDbType.NVarChar, 255) { Value = ResourceFor(conversationId) });
            var outcome = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
            if (outcome is 0 or 1)
            {
                return new Lease(connection, ResourceFor(conversationId));
            }

            await connection.DisposeAsync();
            return null;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private sealed class Lease(SqlConnection connection, string resource) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                await using var command = connection.CreateCommand();
                command.CommandText = ReleaseSql;
                command.Parameters.Add(new SqlParameter("@resource", SqlDbType.NVarChar, 255) { Value = resource });
                await command.ExecuteNonQueryAsync(CancellationToken.None);
            }
            catch (SqlException)
            {
                // The session is gone (broken connection): SQL Server released the lock with it.
            }
            catch (InvalidOperationException)
            {
                // The connection is no longer open: same.
            }
            finally
            {
                await connection.DisposeAsync();
            }
        }
    }
}
