using System.Collections.Concurrent;
using CarRental.Application.Messaging.Contracts;
using CarRental.Application.Messaging.Responder;

namespace CarRental.Application.Messaging.Agent.Turns;

/// <summary>
/// Hands a pending bot turn over to whatever runs it (ADR-0054). The outbox handler of a customer message only calls
/// this: the default runs the turn at once in the handler (hosts without the executor, application tests); the
/// infrastructure replaces it with the bot turn executor, which runs turns in the background, in parallel across
/// conversations and one at a time per conversation, so that model calls never hold up the outbox.
/// </summary>
public interface IBotTurnDispatcher
{
    Task DispatchAsync(Guid conversationId, Guid messageId, string? correlationId, CancellationToken cancellationToken);
}

/// <summary>
/// Runs the turn inside the caller (the 4A behaviour): prepare, respond, apply, each step working for the agency of the
/// conversation; nothing for an agency WhatsApp is not open for — suspended, module off, or not served by the platform's
/// number (its messages wait). An exception of the
/// responder goes up to the outbox, which retries the message later.
/// </summary>
internal sealed class InlineBotTurnDispatcher(MessagingScopes scopes) : IBotTurnDispatcher
{
    public async Task DispatchAsync(Guid conversationId, Guid messageId, string? correlationId, CancellationToken cancellationToken)
    {
        if (await scopes.AgencyOfConversationAsync(conversationId, cancellationToken) is not { } agencyId
            || !await scopes.IsOpenAsync(agencyId, cancellationToken))
        {
            return;
        }

        var turn = await scopes.RunAsync<ConversationTurns, ConversationTurn?>(
            (turns, ct) => turns.PrepareAsync(conversationId, messageId, ct),
            correlationId,
            cancellationToken,
            agencyId);
        if (turn is null)
        {
            return;
        }

        var reply = await scopes.RunOnceAsync<IConversationResponder, ConversationReply>(
            (responder, ct) => responder.RespondAsync(turn, ct),
            correlationId,
            cancellationToken,
            agencyId) ?? ConversationReply.None;

        await scopes.RunAsync<ConversationTurns, bool>((turns, ct) => turns.ApplyAsync(turn, reply, ct), correlationId, cancellationToken, agencyId);
    }
}

/// <summary>
/// Serializes the bot turns of a conversation: never two turns of one conversation at once, on any instance. The
/// infrastructure takes a SQL Server application lock (<c>sp_getapplock</c> on <c>conversation:{id}</c>, session-owned,
/// on a connection of its own); the default only works within one process.
/// </summary>
public interface IConversationTurnLock
{
    /// <summary>The lock of the conversation, or <see langword="null"/> when another turn holds it (no wait).</summary>
    Task<IAsyncDisposable?> TryAcquireAsync(Guid conversationId, CancellationToken cancellationToken);
}

/// <summary>A lock per conversation within this process (tests and hosts without SQL Server).</summary>
internal sealed class InProcessConversationTurnLock : IConversationTurnLock
{
    private readonly ConcurrentDictionary<Guid, byte> _held = new();

    public Task<IAsyncDisposable?> TryAcquireAsync(Guid conversationId, CancellationToken cancellationToken) =>
        Task.FromResult<IAsyncDisposable?>(_held.TryAdd(conversationId, 0) ? new Lease(_held, conversationId) : null);

    private sealed class Lease(ConcurrentDictionary<Guid, byte> held, Guid conversationId) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            held.TryRemove(conversationId, out _);
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>
/// Which customers the bot answers. Everyone by default; a pilot list (<c>Ai:PilotNumbers</c>) restricts the bot to
/// some numbers during the rollout — the others wait for an agent in the shared inbox, as when the bot is off.
/// </summary>
public interface IBotAudience
{
    bool Includes(string whatsAppNumber);
}

internal sealed class EveryoneBotAudience : IBotAudience
{
    public bool Includes(string whatsAppNumber) => true;
}

/// <summary>
/// Settings of the bot turn executor (section <c>BotTurns</c>): turns run at once on how many conversations, how long a
/// conversation stays quiet before its turn (a burst of messages is answered once), how often the queue is swept as a
/// safety net, and the retries of a failed turn before the hand-over to a human.
/// </summary>
public sealed class BotTurnOptions
{
    public const string SectionName = "BotTurns";

    /// <summary>Conversations whose turns run at the same time on this instance (4).</summary>
    public int MaxParallelism { get; set; } = 4;

    /// <summary>A turn starts once the customer stopped writing for this long (2 seconds): a burst is answered once.</summary>
    public TimeSpan QuietPeriod { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Without a signal, the queue is swept this often (15 seconds): restarts, other instances, due retries.</summary>
    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Delays before the attempts after a failure, separated by <c>;</c> (<c>00:00:20;00:01:00;00:05:00</c>: 20 s, 1 min,
    /// 5 min, growing): their count is the number of retries before the hand-over.
    /// </summary>
    public string RetryDelays { get; set; } = "00:00:20;00:01:00;00:05:00";

    /// <summary>The parsed <see cref="RetryDelays"/>; <see langword="null"/> when one is not a positive duration.</summary>
    public IReadOnlyList<TimeSpan>? ParsedRetryDelays()
    {
        var delays = new List<TimeSpan>();
        foreach (var part in (RetryDelays ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!TimeSpan.TryParse(part, System.Globalization.CultureInfo.InvariantCulture, out var delay) || delay <= TimeSpan.Zero)
            {
                return null;
            }

            delays.Add(delay);
        }

        return delays;
    }

    /// <summary>Attempts of a turn in all: the first one and the retries.</summary>
    public int MaxAttempts => (ParsedRetryDelays()?.Count ?? 0) + 1;

    /// <summary>The delay before the attempt that follows <paramref name="failedAttempts"/> failures.</summary>
    public TimeSpan DelayAfter(int failedAttempts)
    {
        var delays = ParsedRetryDelays() ?? [];
        return delays.Count == 0 ? TimeSpan.Zero : delays[Math.Clamp(failedAttempts - 1, 0, delays.Count - 1)];
    }
}
