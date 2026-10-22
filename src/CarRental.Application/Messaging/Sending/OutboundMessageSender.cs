using System.Globalization;
using CarRental.Application.Common.Outbox;
using CarRental.Application.Common.Persistence;
using CarRental.Application.Customers.Contracts;
using CarRental.Application.Referential.Contracts;
using CarRental.Domain.Common;
using CarRental.Domain.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CarRental.Application.Messaging.Sending;

/// <summary>
/// Sends a queued outbound message through <see cref="IWhatsAppClient"/> and records the outcome (Meta id, or the
/// refusal). Checked again at the time of sending: nothing leaves for an agency suspended or whose WhatsApp module was
/// switched off meanwhile (ADR-0067), a bot message is dropped when an agent took the conversation over since it was
/// queued (the last message of a hand-over by the bot itself still leaves), a free-form message when the 24-hour window
/// closed meanwhile (an outbox retry hours later), and a booking notification when the person withdrew the WhatsApp
/// consent (or was anonymized) since it was queued. Run in a scope working for the agency of the message.
/// </summary>
/// <remarks>
/// <para>At most once: the message is marked <see cref="MessageStatus.Sending"/> and saved before the Graph API is
/// called (row version: of two senders, or of a sender and a takeover cancelling the message, one only goes on). From
/// then on the call and the save of its outcome are no longer cancelled (the client's own timeout bounds them), so that
/// a shutdown never loses an outcome; an outcome lost anyway (crash, database failure) leaves the message
/// <see cref="MessageStatus.Sending"/>, and the next attempt records it as unknown instead of sending it again.</para>
/// <para>Retries only where safe: a transient outcome (the message certainly did not go out) puts the message back in
/// the queue and throws <see cref="WhatsAppUnavailableException"/> so that the outbox tries again later, up to
/// <see cref="MaxSendAttempts"/> attempts; a refusal or an unknown outcome (the request may have reached Meta) is final.</para>
/// </remarks>
internal sealed partial class OutboundMessageSender(
    IAppDbContext db,
    IWhatsAppClient client,
    ICustomersQueries customers,
    IAgencyDirectory agencies,
    TimeProvider clock,
    ILogger<OutboundMessageSender> logger)
{
    /// <summary>Attempts of a transient failure before the message is marked failed (the outbox spaces them 1, 2, 4, 8 minutes).</summary>
    public const int MaxSendAttempts = 5;

    public async Task<MessageStatus?> SendAsync(Guid messageId, int attempt, CancellationToken cancellationToken)
    {
        var message = await db.Set<Message>().FirstOrDefaultAsync(m => m.Id == messageId, cancellationToken);
        if (message is null || message.Direction != MessageDirection.Outbound)
        {
            return message?.Status;
        }

        if (message.Status == MessageStatus.Sending)
        {
            // A previous attempt called the Graph API and lost its outcome: the message may have left, it is not sent again.
            LogOutcomeLost(message.Kind.ToString());
            message.MarkNotSent(MessagingErrors.SendOutcomeUnknown, null, null, clock.GetUtcNow());
            await db.SaveChangesAsync(cancellationToken);
            return message.Status;
        }

        if (message.Status != MessageStatus.Queued)
        {
            // Sent (or dropped) by a previous delivery of the outbox message.
            return message.Status;
        }

        var conversation = await db.Set<Conversation>().AsNoTracking().FirstAsync(c => c.Id == message.ConversationId, cancellationToken);
        var now = clock.GetUtcNow();
        if (!client.IsEnabled)
        {
            message.MarkNotSent(MessagingErrors.Disabled, null, null, now);
        }
        else if (!await agencies.HasModuleAsync(message.AgencyId, AgencyModules.WhatsApp, cancellationToken))
        {
            message.MarkNotSent(MessagingErrors.ModuleOff, null, null, now);
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
        else if (message.Author == MessageAuthor.System && !await HasConsentAsync(message.Id, cancellationToken))
        {
            message.Cancel(MessagingErrors.ConsentWithdrawn, now);
        }
        else
        {
            return await CallGraphAsync(message, conversation.WhatsAppNumber, attempt, cancellationToken);
        }

        await db.SaveChangesAsync(cancellationToken);
        return message.Status;
    }

    private async Task<MessageStatus?> CallGraphAsync(Message message, string number, int attempt, CancellationToken cancellationToken)
    {
        message.MarkSending();
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Cancelled by a takeover, or taken by another sender, meanwhile: nothing leaves from here.
            LogChangedMeanwhile(message.Kind.ToString());
            return null;
        }

        // The request may leave from here on: neither the call nor the save of its outcome is cancelled any more.
        var result = await client.SendAsync(new WhatsAppSendRequest(WhatsAppNumbers.ToWaId(number), message.Content), CancellationToken.None);
        var retry = Apply(message, result, attempt, clock.GetUtcNow());
        await db.SaveChangesAsync(CancellationToken.None);
        if (retry)
        {
            throw new WhatsAppUnavailableException(string.Create(
                CultureInfo.InvariantCulture,
                $"The WhatsApp service is temporarily unavailable (attempt {attempt}, Meta error {result.ErrorCode?.ToString(CultureInfo.InvariantCulture) ?? "none"})."));
        }

        return message.Status;
    }

    /// <summary>Records the outcome of the call; <see langword="true"/> when the message went back to the queue for a later attempt.</summary>
    private bool Apply(Message message, WhatsAppSendResult result, int attempt, DateTimeOffset now)
    {
        switch (result.Outcome)
        {
            case WhatsAppSendOutcome.Accepted when !string.IsNullOrWhiteSpace(result.MetaMessageId):
                message.MarkAccepted(result.MetaMessageId, now);
                return false;
            case WhatsAppSendOutcome.Rejected:
                LogRejected(message.Kind.ToString(), result.ErrorCode, result.ErrorTitle ?? string.Empty);
                message.MarkNotSent(MessagingErrors.SendRejected, result.ErrorCode, result.ErrorTitle, now);
                return false;
            case WhatsAppSendOutcome.Transient when attempt < MaxSendAttempts:
                LogTransient(attempt, result.ErrorCode);
                message.ReturnToQueue();
                return true;
            case WhatsAppSendOutcome.Transient:
                message.MarkNotSent(MessagingErrors.SendUnavailable, result.ErrorCode, result.ErrorTitle, now);
                return false;
            default:
                LogUnknown(message.Kind.ToString());
                message.MarkNotSent(MessagingErrors.SendOutcomeUnknown, result.ErrorCode, result.ErrorTitle, now);
                return false;
        }
    }

    /// <summary>
    /// Whether the person of a booking notification still consents to WhatsApp notifications (read from the Customers
    /// module at the time of sending); a system message that is not a booking notification needs no consent.
    /// </summary>
    private async Task<bool> HasConsentAsync(Guid messageId, CancellationToken cancellationToken)
    {
        var personId = await db.Set<BookingNotification>()
            .AsNoTracking()
            .Where(n => n.MessageId == messageId)
            .Select(n => (Guid?)n.PersonId)
            .FirstOrDefaultAsync(cancellationToken);
        return personId is not { } id || await customers.HasConsentAsync(id, ConsentPurposeInfo.WhatsAppNotifications, cancellationToken);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "WhatsApp {Kind} message refused by Meta: error {ErrorCode} ({ErrorTitle}).")]
    private partial void LogRejected(string kind, int? errorCode, string errorTitle);

    [LoggerMessage(Level = LogLevel.Warning, Message = "WhatsApp unavailable on attempt {Attempt} (Meta error {ErrorCode}): the outbox retries later.")]
    private partial void LogTransient(int attempt, int? errorCode);

    [LoggerMessage(Level = LogLevel.Error, Message = "WhatsApp {Kind} message: no answer from Meta, the outcome is unknown; not retried to avoid a duplicate.")]
    private partial void LogUnknown(string kind);

    [LoggerMessage(Level = LogLevel.Error, Message = "WhatsApp {Kind} message: the outcome of a previous send was lost (interrupted); not sent again to avoid a duplicate.")]
    private partial void LogOutcomeLost(string kind);

    [LoggerMessage(Level = LogLevel.Information, Message = "WhatsApp {Kind} message changed meanwhile (cancelled by a takeover or sent by another worker): not sent here.")]
    private partial void LogChangedMeanwhile(string kind);
}

/// <summary>
/// Sends each queued outbound message (<see cref="OutboundMessageQueued"/>), in a scope of its own working for the agency
/// of the message, never replayed in-process.
/// </summary>
internal sealed class OutboundMessageQueuedHandler(MessagingScopes scopes) : IOutboxHandler<OutboundMessageQueued>
{
    public async Task HandleAsync(OutboundMessageQueued message, OutboxMessageContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(context);
        if (await scopes.AgencyOfMessageAsync(message.MessageId, cancellationToken) is not { } agencyId)
        {
            // Purged meanwhile: nothing to send.
            return;
        }

        await scopes.RunOnceAsync<OutboundMessageSender, MessageStatus?>(
            (sender, ct) => sender.SendAsync(message.MessageId, context.Attempt, ct),
            context.CorrelationId,
            cancellationToken,
            agencyId);
    }
}
