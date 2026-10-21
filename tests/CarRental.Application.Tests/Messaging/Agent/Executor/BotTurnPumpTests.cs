using System.Collections.Concurrent;
using CarRental.Application.Common.Security;
using CarRental.Application.Messaging.Agent.Turns;
using CarRental.Infrastructure.Messaging.Agent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace CarRental.Application.Tests.Messaging.Agent.Executor;

/// <summary>
/// The bot turn executor of the infrastructure (ADR-0054) on a fake queue and a fake runner: it runs the due turns at
/// once on a signal, up to its parallelism, never twice the same conversation at a time, and leaves a conversation busy
/// elsewhere alone for its cooldown.
/// </summary>
public sealed class BotTurnPumpTests : IAsyncDisposable
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private readonly FakeQueue _queue = new();
    private readonly GatedRunner _runner = new();
    private readonly BotTurnWakeup _wakeup = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2027, 6, 20, 8, 0, 0, TimeSpan.Zero));
    private readonly ServiceProvider _services;
    private BotTurnPump? _pump;

    public BotTurnPumpTests()
    {
        var services = new ServiceCollection();
        services.AddScoped<ScopedUserContext>();
        services.AddSingleton<IBotTurnQueue>(_queue);
        _services = services.BuildServiceProvider();
        _runner.Queue = _queue;
    }

    public async ValueTask DisposeAsync()
    {
        _runner.ReleaseAll();
        if (_pump is not null)
        {
            await _pump.StopAsync(CancellationToken.None);
            _pump.Dispose();
        }

        _wakeup.Dispose();
        await _services.DisposeAsync();
    }

    [Fact]
    public async Task Due_turns_run_at_once_in_parallel_up_to_the_limit()
    {
        var (first, second, third) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
        _queue.Add(first, second, third);
        await StartAsync(maxParallelism: 2);

        _wakeup.Signal();

        await _runner.WaitForRunningAsync(2);
        _runner.Started.ShouldBe([first, second], ignoreOrder: true);
        _runner.Release(first);
        await _runner.WaitForStartedAsync(third);
        _runner.MaxConcurrency.ShouldBe(2);
    }

    [Fact]
    public async Task A_conversation_is_never_run_twice_at_the_same_time()
    {
        var conversation = Guid.CreateVersion7();
        _queue.Add(conversation);
        await StartAsync(maxParallelism: 4);

        _wakeup.Signal();
        await _runner.WaitForStartedAsync(conversation);
        for (var i = 0; i < 5; i++)
        {
            _wakeup.Signal();
        }

        await _queue.WaitForReadsAsync(3);
        _runner.Started.ShouldBe([conversation], "still pending, but running here");
    }

    [Fact]
    public async Task A_conversation_busy_elsewhere_rests_before_its_next_try()
    {
        var conversation = Guid.CreateVersion7();
        _queue.Add(conversation);
        _runner.Busy.Add(conversation);
        await StartAsync(maxParallelism: 4);

        _wakeup.Signal();
        await _runner.WaitForStartedAsync(conversation);
        await _runner.WaitForEndedAsync(1);
        _runner.Busy.Clear();
        var reads = _queue.Reads;
        _wakeup.Signal();
        await _queue.WaitForReadsAsync(reads + 1);
        _runner.Started.Count.ShouldBe(1, "resting for its cooldown");

        _clock.Advance(TimeSpan.FromSeconds(3));
        _wakeup.Signal();

        await _runner.WaitForStartedCountAsync(2);
    }

    private async Task StartAsync(int maxParallelism)
    {
        var options = Options.Create(new BotTurnOptions { MaxParallelism = maxParallelism, QuietPeriod = TimeSpan.Zero, SweepInterval = TimeSpan.FromMinutes(10) });
        _pump = new BotTurnPump(_services.GetRequiredService<IServiceScopeFactory>(), _runner, _wakeup, options, _clock, NullLogger<BotTurnPump>.Instance);
        await _pump.StartAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>Conversations due until their turn completes; counts the reads of the queue.</summary>
    private sealed class FakeQueue : IBotTurnQueue
    {
        private readonly List<Guid> _due = [];
        private int _reads;

        public int Reads => Volatile.Read(ref _reads);

        public void Add(params Guid[] conversations)
        {
            lock (_due)
            {
                _due.AddRange(conversations);
            }
        }

        public void Complete(Guid conversation)
        {
            lock (_due)
            {
                _due.Remove(conversation);
            }
        }

        public Task<IReadOnlyList<Guid>> DueAsync(BotTurnOptions options, IReadOnlyCollection<Guid> running, int take, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _reads);
            lock (_due)
            {
                return Task.FromResult<IReadOnlyList<Guid>>([.. _due.Where(id => !running.Contains(id)).Take(take)]);
            }
        }

        public Task<DateTimeOffset?> NextDueAtAsync(BotTurnOptions options, IReadOnlyCollection<Guid> excluded, CancellationToken cancellationToken)
        {
            lock (_due)
            {
                return Task.FromResult(_due.Any(id => !excluded.Contains(id)) ? DateTimeOffset.UnixEpoch : (DateTimeOffset?)null);
            }
        }

        public async Task WaitForReadsAsync(int count)
        {
            var deadline = DateTime.UtcNow + Patience;
            while (Reads < count)
            {
                DateTime.UtcNow.ShouldBeLessThan(deadline, $"the queue was read {Reads} time(s), expected {count}");
                await Task.Yield();
            }
        }
    }

    /// <summary>A runner whose turns wait until released (or end as busy), recording what ran and when.</summary>
    private sealed class GatedRunner : IBotTurnRunner
    {
        private readonly ConcurrentDictionary<Guid, TaskCompletionSource> _gates = new();
        private readonly List<Guid> _started = [];
        private int _running;
        private int _maxConcurrency;
        private int _ended;

        public FakeQueue? Queue { get; set; }

        public HashSet<Guid> Busy { get; } = [];

        public List<Guid> Started
        {
            get
            {
                lock (_started)
                {
                    return [.. _started];
                }
            }
        }

        public int MaxConcurrency => Volatile.Read(ref _maxConcurrency);

        public async Task<BotTurnRunOutcome> RunAsync(Guid conversationId, CancellationToken cancellationToken)
        {
            lock (_started)
            {
                _started.Add(conversationId);
            }

            var running = Interlocked.Increment(ref _running);
            InterlockedMax(ref _maxConcurrency, running);
            try
            {
                if (Busy.Contains(conversationId))
                {
                    return BotTurnRunOutcome.Busy;
                }

                await _gates.GetOrAdd(conversationId, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task.WaitAsync(cancellationToken);
                Queue?.Complete(conversationId);
                return BotTurnRunOutcome.Completed;
            }
            finally
            {
                Interlocked.Decrement(ref _running);
                Interlocked.Increment(ref _ended);
            }
        }

        public void Release(Guid conversationId) =>
            _gates.GetOrAdd(conversationId, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();

        public void ReleaseAll()
        {
            foreach (var gate in _gates.Values)
            {
                gate.TrySetResult();
            }
        }

        public Task WaitForRunningAsync(int count) => WaitAsync(() => Volatile.Read(ref _running) >= count, $"{count} turns running");

        public Task WaitForStartedAsync(Guid conversationId) => WaitAsync(() => Started.Contains(conversationId), "the turn started");

        public Task WaitForStartedCountAsync(int count) => WaitAsync(() => Started.Count >= count, $"{count} turns started");

        public Task WaitForEndedAsync(int count) => WaitAsync(() => Volatile.Read(ref _ended) >= count, $"{count} turns ended");

        private static async Task WaitAsync(Func<bool> done, string what)
        {
            var deadline = DateTime.UtcNow + Patience;
            while (!done())
            {
                DateTime.UtcNow.ShouldBeLessThan(deadline, $"Timed out waiting for {what}.");
                await Task.Yield();
            }
        }

        private static void InterlockedMax(ref int target, int value)
        {
            int current;
            while ((current = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, current) != current)
            {
                // Retry until the maximum is stored.
            }
        }
    }
}
