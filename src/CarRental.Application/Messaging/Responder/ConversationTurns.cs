using CarRental.Application.Common.Persistence;
using CarRental.Application.Messaging.Contracts;
using CarRental.Domain.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CarRental.Application.Messaging.Responder;

/// <summary>
/// The two database steps of a bot turn, around the call to <see cref="IConversationResponder"/> (each in a scope of
/// its own): <see cref="PrepareAsync"/> builds the turn — or decides there is none — and <see cref="ApplyAsync"/> applies
/// the reply on fresh data, so that a takeover committed meanwhile always wins.
/// </summary>
internal sealed partial class ConversationTurns(IAppDbContext db, TimeProvider clock, ILogger<ConversationTurns> logger)
{
    /// <summary>
    /// The turn of <paramref name="messageId"/>, or <see langword="null"/> when there is nothing to answer: the message was
    /// handled already, a newer customer message will answer it with its own turn, or the conversation is in human mode
    /// (its pending messages are then marked silenced).
    /// </summary>
    public async Task<ConversationTurn?> PrepareAsync(Guid conversationId, Guid messageId, CancellationToken cancellationToken)
    {
        var conversation = await db.Set<Conversation>().FirstOrDefaultAsync(c => c.Id == conversationId, cancellationToken);
        var pending = await PendingAsync(conversationId, cancellationToken);
        var trigger = pending.Find(m => m.Id == messageId);
        if (conversation is null || trigger is null)
        {
            return null;
        }

        if (conversation.Mode != ConversationMode.Bot)
        {
            Silence(pending);
            await db.SaveChangesAsync(cancellationToken);
            return null;
        }

        if (pending[^1].Id != trigger.Id)
        {
            // A newer customer message is pending: its turn answers both.
            return null;
        }

        var now = clock.GetUtcNow();
        if (!conversation.IsWindowOpen(now))
        {
            // Processed more than 24 hours late (long outage): a free-form reply is no longer possible.
            foreach (var message in pending)
            {
                message.CompleteBotTurn(BotTurnState.NoReply);
            }

            await db.SaveChangesAsync(cancellationToken);
            LogWindowClosed();
            return null;
        }

        var pendingIds = pending.Select(m => m.Id).ToList();
        var history = await db.Set<Message>()
            .AsNoTracking()
            .Where(m => m.ConversationId == conversationId && !pendingIds.Contains(m.Id))
            .OrderByDescending(m => m.CreatedAtUtc)
            .ThenByDescending(m => m.Id)
            .Take(ConversationTurn.MaxHistory)
            .ToListAsync(cancellationToken);
        history.Reverse();

        return new ConversationTurn(
            conversation.Id,
            trigger.Id,
            conversation.WhatsAppNumber,
            conversation.ProfileName,
            conversation.PersonId,
            conversation.CustomerId,
            conversation.BookingId,
            now,
            conversation.WindowClosesAtUtc!.Value,
            conversation.Need.ToInfo(),
            conversation.Usage.ToInfo(),
            [.. pending.Select(m => m.ToInfo())],
            [.. history.Select(m => m.ToInfo())]);
    }

    /// <summary>
    /// Applies the reply of <paramref name="turn"/> on the current state: in bot mode, the usage, the need, the single
    /// message (queued for sending) and the hand-over; after a takeover (even one already handed back: the takeover
    /// silenced the messages of the turn), nothing but the usage and the silenced marks. The messages of the turn are
    /// marked answered (or superseded). Returns whether a message was queued.
    /// </summary>
    public async Task<bool> ApplyAsync(ConversationTurn turn, ConversationReply reply, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(turn);
        ArgumentNullException.ThrowIfNull(reply);
        var conversation = await db.Set<Conversation>().FirstOrDefaultAsync(c => c.Id == turn.ConversationId, cancellationToken);
        if (conversation is null)
        {
            return false;
        }

        // The model calls happened whatever comes next: their cost counts.
        if (reply.Usage is { } usage)
        {
            conversation.RecordBotTurn(usage.ToDomain());
        }

        var pending = await PendingAsync(turn.ConversationId, cancellationToken);
        var trigger = pending.Find(m => m.Id == turn.TriggerMessageId);
        var turnMessageIds = turn.NewMessages.Select(m => m.Id).ToHashSet();
        var ofTurn = pending.Where(m => turnMessageIds.Contains(m.Id)).ToList();
        if (trigger is null || conversation.Mode != ConversationMode.Bot)
        {
            Silence(ofTurn);
            await db.SaveChangesAsync(cancellationToken);
            LogSilenced();
            return false;
        }

        var now = clock.GetUtcNow();

        if (reply.Need is { } need)
        {
            var updated = need.ToDomain();
            if (updated.IsSuccess)
            {
                conversation.UpdateNeed(updated.Value);
            }
            else
            {
                LogReplyPartRejected("need", updated.Error.Code);
            }
        }

        var replied = false;
        if (reply.Message is { } content)
        {
            var queued = content.ToDomain().Bind(domain => conversation.ReplyAsBot(domain, now));
            if (queued.IsSuccess)
            {
                db.Set<Message>().Add(queued.Value);
                replied = true;
            }
            else
            {
                LogReplyPartRejected("message", queued.Error.Code);
            }
        }

        if (!string.IsNullOrWhiteSpace(reply.HandOverReason))
        {
            var reason = reply.HandOverReason.Trim();
            var handedOver = conversation.HandOverToHuman(
                reason.Length > Conversation.HandoverReasonMaxLength ? reason[..Conversation.HandoverReasonMaxLength] : reason,
                now);
            if (handedOver.IsFailure)
            {
                LogReplyPartRejected("hand-over", handedOver.Error.Code);
            }
        }

        foreach (var message in ofTurn)
        {
            message.CompleteBotTurn(message.Id == trigger.Id
                ? replied ? BotTurnState.Replied : BotTurnState.NoReply
                : BotTurnState.Superseded);
        }

        await db.SaveChangesAsync(cancellationToken);
        return replied;
    }

    private static void Silence(IEnumerable<Message> messages)
    {
        foreach (var message in messages)
        {
            message.CompleteBotTurn(BotTurnState.Silenced);
        }
    }

    /// <summary>The customer messages of the conversation waiting for the bot, oldest first (tracked).</summary>
    private Task<List<Message>> PendingAsync(Guid conversationId, CancellationToken cancellationToken) =>
        db.Set<Message>()
            .Where(m => m.ConversationId == conversationId && m.Direction == MessageDirection.Inbound && m.BotTurn == BotTurnState.Pending)
            .OrderBy(m => m.CreatedAtUtc)
            .ThenBy(m => m.Id)
            .ToListAsync(cancellationToken);

    [LoggerMessage(Level = LogLevel.Information, Message = "WhatsApp bot turn silenced: an agent took the conversation over.")]
    private partial void LogSilenced();

    [LoggerMessage(Level = LogLevel.Warning, Message = "WhatsApp bot turn processed more than 24 hours after the customer's message: no reply.")]
    private partial void LogWindowClosed();

    [LoggerMessage(Level = LogLevel.Warning, Message = "WhatsApp bot reply: the {Part} was rejected ({ErrorCode}).")]
    private partial void LogReplyPartRejected(string part, string errorCode);
}
