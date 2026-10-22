using CarRental.Application.Common.Persistence;
using CarRental.Application.Messaging.Contracts;
using CarRental.Application.Messaging.Responder;
using CarRental.Domain.Messaging;
using CarRental.Domain.Messaging.Agent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CarRental.Application.Messaging.Agent.Turns;

/// <summary>What became of a run of <see cref="BotTurnRunner"/>.</summary>
public enum BotTurnRunOutcome
{
    /// <summary>Nothing to answer (handled already, human mode, window closed).</summary>
    NoTurn = 1,

    /// <summary>The turn ran and its reply was applied.</summary>
    Completed = 2,

    /// <summary>Another turn of the conversation holds its lock (this instance or another one).</summary>
    Busy = 3,

    /// <summary>The responder failed; the turn is due again later.</summary>
    RetryScheduled = 4,

    /// <summary>The responder failed for the last time: the conversation was handed over to a human.</summary>
    HandedOver = 5,
}

/// <summary>
/// Runs the pending bot turn of one conversation (ADR-0054), under the conversation's turn lock, each step working for
/// the agency of the conversation (ADR-0067): prepares the turn of the latest pending customer message (it answers the
/// whole burst), calls the responder outside any transaction, applies its reply on fresh data (a takeover committed
/// meanwhile wins). A failure of the responder schedules a retry with a growing delay; after the last attempt the
/// conversation is handed over to a human with a polite message — a turn is never dropped silently. The state lives in
/// the database (pending messages, <see cref="BotTurnRetry"/>), so a restart loses nothing. An agency suspended or whose
/// WhatsApp module is off gets no turn: its messages wait for the module (the queue leaves them out).
/// </summary>
public interface IBotTurnRunner
{
    Task<BotTurnRunOutcome> RunAsync(Guid conversationId, CancellationToken cancellationToken);
}

/// <summary>The queue of the bot turn executor (see <see cref="BotTurnQueue"/>).</summary>
public interface IBotTurnQueue
{
    /// <summary>Conversations whose turn is due now, the oldest first, except those already running here.</summary>
    Task<IReadOnlyList<Guid>> DueAsync(BotTurnOptions options, IReadOnlyCollection<Guid> running, int take, CancellationToken cancellationToken);

    /// <summary>When the next pending turn becomes due, except those of <paramref name="excluded"/>, or <see langword="null"/> when none waits.</summary>
    Task<DateTimeOffset?> NextDueAtAsync(BotTurnOptions options, IReadOnlyCollection<Guid> excluded, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IBotTurnRunner"/>
internal sealed partial class BotTurnRunner(
    MessagingScopes scopes,
    IConversationTurnLock turnLock,
    IOptions<BotTurnOptions> options,
    ILogger<BotTurnRunner> logger) : IBotTurnRunner
{
    /// <summary>Reason of the hand-over after the last failed attempt (shown to the agents).</summary>
    public const string UnavailableReason = "Assistant IA indisponible après plusieurs tentatives : à reprendre par un agent.";

    public async Task<BotTurnRunOutcome> RunAsync(Guid conversationId, CancellationToken cancellationToken)
    {
        var lease = await turnLock.TryAcquireAsync(conversationId, cancellationToken);
        if (lease is null)
        {
            return BotTurnRunOutcome.Busy;
        }

        await using (lease)
        {
            if (await scopes.AgencyOfConversationAsync(conversationId, cancellationToken) is not { } agencyId
                || !await scopes.IsOpenAsync(agencyId, cancellationToken))
            {
                return BotTurnRunOutcome.NoTurn;
            }

            var turn = await scopes.RunAsync<ConversationTurns, ConversationTurn?>(
                (turns, ct) => turns.PrepareLatestAsync(conversationId, ct),
                correlationId: null,
                cancellationToken,
                agencyId);
            if (turn is null)
            {
                await scopes.RunAsync<BotTurnRetries, bool>((retries, ct) => retries.ClearAsync(conversationId, ct), null, cancellationToken, agencyId);
                return BotTurnRunOutcome.NoTurn;
            }

            ConversationReply reply;
            var outcome = BotTurnRunOutcome.Completed;
            try
            {
                reply = await scopes.RunOnceAsync<IConversationResponder, ConversationReply>(
                    (responder, ct) => responder.RespondAsync(turn, ct),
                    correlationId: null,
                    cancellationToken,
                    agencyId) ?? ConversationReply.None;
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                var attempts = await scopes.RunAsync<BotTurnRetries, int>(
                    (retries, ct) => retries.RecordFailureAsync(conversationId, agencyId, options.Value, ct),
                    null,
                    cancellationToken,
                    agencyId);
                LogFailed(exception.GetType().Name, attempts, options.Value.MaxAttempts);
                if (attempts < options.Value.MaxAttempts)
                {
                    return BotTurnRunOutcome.RetryScheduled;
                }

                var language = CustomerLanguageDetector.Detect(turn.NewMessages.Select(m => m.Text));
                reply = new ConversationReply { Message = new OutboundText(AgentMessages.HandOver(language)), HandOverReason = UnavailableReason };
                outcome = BotTurnRunOutcome.HandedOver;
            }

            await scopes.RunAsync<ConversationTurns, bool>((turns, ct) => turns.ApplyAsync(turn, reply, ct), null, cancellationToken, agencyId);
            await scopes.RunAsync<BotTurnRetries, bool>((retries, ct) => retries.ClearAsync(conversationId, ct), null, cancellationToken, agencyId);
            return outcome;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "WhatsApp bot turn failed ({ExceptionType}), attempt {Attempt} of {MaxAttempts}.")]
    private partial void LogFailed(string exceptionType, int attempt, int maxAttempts);
}

/// <summary>The retries of the bot turns (<see cref="BotTurnRetry"/>), each step in a scope of its own.</summary>
internal sealed class BotTurnRetries(IAppDbContext db, TimeProvider clock)
{
    /// <summary>Counts a failed attempt of the conversation of <paramref name="agencyId"/> and schedules the next one; returns the attempts so far.</summary>
    public async Task<int> RecordFailureAsync(Guid conversationId, Guid agencyId, BotTurnOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        var retry = await db.Set<BotTurnRetry>().FirstOrDefaultAsync(r => r.Id == conversationId, cancellationToken);
        if (retry is null)
        {
            retry = BotTurnRetry.For(conversationId, agencyId);
            db.Set<BotTurnRetry>().Add(retry);
        }

        retry.Failed(clock.GetUtcNow(), options.DelayAfter(retry.Attempts + 1));
        await db.SaveChangesAsync(cancellationToken);
        return retry.Attempts;
    }

    public async Task<bool> ClearAsync(Guid conversationId, CancellationToken cancellationToken)
    {
        var retry = await db.Set<BotTurnRetry>().FirstOrDefaultAsync(r => r.Id == conversationId, cancellationToken);
        if (retry is null)
        {
            return false;
        }

        db.Set<BotTurnRetry>().Remove(retry);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }
}

/// <summary>
/// The queue of the bot turn executor, read from the messages themselves: the conversations with a pending customer
/// message whose customer has been quiet for <see cref="BotTurnOptions.QuietPeriod"/> and whose retry, if any, is due —
/// the oldest first —, of the active agencies whose WhatsApp module is on (the others' messages wait for the module).
/// </summary>
internal sealed class BotTurnQueue(IAppDbContext db, MessagingScopes scopes, TimeProvider clock) : IBotTurnQueue
{
    public async Task<IReadOnlyList<Guid>> DueAsync(BotTurnOptions options, IReadOnlyCollection<Guid> running, int take, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(running);
        var open = await scopes.OpenAgenciesAsync(cancellationToken);
        if (open.Count == 0)
        {
            return [];
        }

        var now = clock.GetUtcNow();
        var quietSince = now - options.QuietPeriod;
        var runningIds = running.ToList();
        var pending = db.Set<Message>()
            .Where(m => m.Direction == MessageDirection.Inbound && m.BotTurn == BotTurnState.Pending && open.Contains(m.AgencyId))
            .GroupBy(m => m.ConversationId)
            .Select(g => new { ConversationId = g.Key, Latest = g.Max(m => m.CreatedAtUtc) });
        var due = from p in pending
                  join r in db.Set<BotTurnRetry>() on p.ConversationId equals r.Id into retries
                  from r in retries.DefaultIfEmpty()
                  where p.Latest <= quietSince
                        && (r == null || r.NextAttemptAtUtc == null || r.NextAttemptAtUtc <= now)
                        && !runningIds.Contains(p.ConversationId)
                  orderby p.Latest
                  select p.ConversationId;
        return await due.Take(take).ToListAsync(cancellationToken);
    }

    /// <summary>
    /// When the next pending turn becomes due (quiet period over, retry time reached), except those of
    /// <paramref name="excluded"/> (running or resting), or <see langword="null"/> when none waits.
    /// </summary>
    public async Task<DateTimeOffset?> NextDueAtAsync(BotTurnOptions options, IReadOnlyCollection<Guid> excluded, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(excluded);
        var open = await scopes.OpenAgenciesAsync(cancellationToken);
        if (open.Count == 0)
        {
            return null;
        }

        var excludedIds = excluded.ToList();
        var latest = await db.Set<Message>()
            .Where(m => m.Direction == MessageDirection.Inbound && m.BotTurn == BotTurnState.Pending && open.Contains(m.AgencyId) && !excludedIds.Contains(m.ConversationId))
            .GroupBy(m => m.ConversationId)
            .Select(g => new { ConversationId = g.Key, Latest = g.Max(m => m.CreatedAtUtc) })
            .ToListAsync(cancellationToken);
        if (latest.Count == 0)
        {
            return null;
        }

        var ids = latest.Select(l => l.ConversationId).ToList();
        var retries = await db.Set<BotTurnRetry>()
            .Where(r => ids.Contains(r.Id) && r.NextAttemptAtUtc != null)
            .ToDictionaryAsync(r => r.Id, r => r.NextAttemptAtUtc!.Value, cancellationToken);
        return latest
            .Select(l =>
            {
                var quiet = l.Latest + options.QuietPeriod;
                return retries.TryGetValue(l.ConversationId, out var retryAt) && retryAt > quiet ? retryAt : quiet;
            })
            .Min();
    }
}
