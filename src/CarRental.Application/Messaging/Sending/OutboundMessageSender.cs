using CarRental.Application.Common.Outbox;
using CarRental.Application.Common.Persistence;
using CarRental.Domain.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CarRental.Application.Messaging.Sending;

/// <summary>
/// Sends a queued outbound message through <see cref="IWhatsAppClient"/> and records the outcome (Meta id, or the
/// refusal). Checked again at the time of sending: a bot message is dropped when an agent took the conversation over
/// since it was queued (the last message of a hand-over by the bot itself still leaves), and a free-form message when
/// the 24-hour window closed meanwhile (an outbox retry hours later).
/// </summary>
/// <remarks>
/// Retries only where safe: a transient outcome (the message certainly did not go out) throws
/// <see cref="WhatsAppUnavailableException"/> so that the outbox tries again later, up to <see cref="MaxSendAttempts"/>
/// attempts; a refusal or an unknown outcome (the request may have reached Meta) is final, to never send twice.
/// </remarks>
internal sealed partial class OutboundMessageSender(
    IAppDbContext db,
    IWhatsAppClient client,
    TimeProvider clock,
    ILogger<OutboundMessageSender> logger)
{
    /// <summary>Attempts of a transient failure before the message is marked failed (the outbox spaces them 1, 2, 4, 8 minutes).</summary>
    public const int MaxSendAttempts = 5;

    public async Task<MessageStatus?> SendAsync(Guid messageId, int attempt, CancellationToken cancellationToken)
    {
        var message = await db.Set<Message>().FirstOrDefaultAsync(m => m.Id == messageId, cancellationToken);
        if (message is null || message.Direction != MessageDirection.Outbound || message.Status != MessageStatus.Queued)
        {
            // Sent (or dropped) by a previous delivery of the outbox message.
            return message?.Status;
        }

        var conversation = await db.Set<Conversation>().AsNoTracking().FirstAsync(c => c.Id == message.ConversationId, cancellationToken);
        var now = clock.GetUtcNow();
        if (!client.IsEnabled)
        {
            message.MarkNotSent(MessagingErrors.Disabled, null, null, now);
        }
        else if (message.Author == MessageAuthor.Bot && conversation is { Mode: ConversationMode.Human, HumanAgentId: not null })
        {
            // An agent took over since the bot queued it (the bot's own hand-over message still leaves).
            message.Cancel(MessagingErrors.BotSilenced, now);
        }
        else if (message.Content.IsFreeForm && !conversation.IsWindowOpen(now))
        {
            message.Cancel(MessagingErrors.WindowClosed, now);
        }
        else
        {
            var result = await client.SendAsync(new WhatsAppSendRequest(WhatsAppNumbers.ToWaId(conversation.WhatsAppNumber), message.Content), cancellationToken);
            Apply(message, result, attempt, clock.GetUtcNow());
        }

        await db.SaveChangesAsync(cancellationToken);
        return message.Status;
    }

    private void Apply(Message message, WhatsAppSendResult result, int attempt, DateTimeOffset now)
    {
        switch (result.Outcome)
        {
            case WhatsAppSendOutcome.Accepted when !string.IsNullOrWhiteSpace(result.MetaMessageId):
                message.MarkAccepted(result.MetaMessageId, now);
                break;
            case WhatsAppSendOutcome.Rejected:
                LogRejected(message.Kind.ToString(), result.ErrorCode, result.ErrorTitle ?? string.Empty);
                message.MarkNotSent(MessagingErrors.SendRejected, result.ErrorCode, result.ErrorTitle, now);
                break;
            case WhatsAppSendOutcome.Transient when attempt < MaxSendAttempts:
                LogTransient(attempt, result.ErrorCode);
                throw new WhatsAppUnavailableException(
                    $"The WhatsApp service is temporarily unavailable (attempt {attempt}, Meta error {result.ErrorCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none"}).");
            case WhatsAppSendOutcome.Transient:
                message.MarkNotSent(MessagingErrors.SendUnavailable, result.ErrorCode, result.ErrorTitle, now);
                break;
            default:
                LogUnknown(message.Kind.ToString());
                message.MarkNotSent(MessagingErrors.SendOutcomeUnknown, result.ErrorCode, result.ErrorTitle, now);
                break;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "WhatsApp {Kind} message refused by Meta: error {ErrorCode} ({ErrorTitle}).")]
    private partial void LogRejected(string kind, int? errorCode, string errorTitle);

    [LoggerMessage(Level = LogLevel.Warning, Message = "WhatsApp unavailable on attempt {Attempt} (Meta error {ErrorCode}): the outbox retries later.")]
    private partial void LogTransient(int attempt, int? errorCode);

    [LoggerMessage(Level = LogLevel.Error, Message = "WhatsApp {Kind} message: no answer from Meta, the outcome is unknown; not retried to avoid a duplicate.")]
    private partial void LogUnknown(string kind);
}

/// <summary>Sends each queued outbound message (<see cref="OutboundMessageQueued"/>), in a scope of its own, never replayed in-process.</summary>
internal sealed class OutboundMessageQueuedHandler(MessagingScopes scopes) : IOutboxHandler<OutboundMessageQueued>
{
    public Task HandleAsync(OutboundMessageQueued message, OutboxMessageContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(context);
        return scopes.RunOnceAsync<OutboundMessageSender, MessageStatus?>(
            (sender, ct) => sender.SendAsync(message.MessageId, context.Attempt, ct),
            context.CorrelationId,
            cancellationToken);
    }
}
