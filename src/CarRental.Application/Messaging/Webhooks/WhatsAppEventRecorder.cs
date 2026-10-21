using CarRental.Application.Common.Persistence;
using CarRental.Application.Customers.Contracts;
using CarRental.Domain.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CarRental.Application.Messaging.Webhooks;

/// <summary>
/// Records one event of a webhook delivery (run by <see cref="MessagingScopes"/>, one scope per event):
/// <list type="bullet">
/// <item>a customer message, once per Meta id: into the conversation of its number (created on first contact and then
/// linked to the person who has this phone, when exactly one person has it), which asks for a bot turn in bot mode;</item>
/// <item>a delivery status, once per message and status: onto the message sent with this Meta id (statuses of messages
/// the platform did not send are ignored).</item>
/// </list>
/// </summary>
internal sealed partial class WhatsAppEventRecorder(
    IAppDbContext db,
    ICustomersQueries customers,
    TimeProvider clock,
    ILogger<WhatsAppEventRecorder> logger)
{
    /// <summary>Records the message; <see langword="false"/> for a duplicate or an unusable sender.</summary>
    public async Task<bool> RecordMessageAsync(InboundWhatsAppMessage inbound, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(inbound);
        var metaId = inbound.MetaMessageId.Trim();
        if (metaId.Length is 0 or > Message.MetaMessageIdMaxLength)
        {
            // Meta ids are a few dozen characters: one that cannot be stored as is could not be deduplicated either.
            LogInvalidMetaId();
            return false;
        }

        if (await db.Set<Message>().AnyAsync(m => m.MetaMessageId == metaId, cancellationToken))
        {
            LogDuplicateMessage();
            return false;
        }

        var number = WhatsAppNumbers.Normalize(inbound.From);
        if (number.IsFailure)
        {
            LogInvalidSender();
            return false;
        }

        var now = clock.GetUtcNow();
        var conversation = await db.Set<Conversation>().FirstOrDefaultAsync(c => c.WhatsAppNumber == number.Value, cancellationToken);
        if (conversation is null)
        {
            conversation = Conversation.Start(number.Value, inbound.ProfileName, now).Value;
            await LinkKnownPersonAsync(conversation, cancellationToken);
            db.Set<Conversation>().Add(conversation);
        }

        var message = conversation.ReceiveCustomerMessage(metaId, inbound.Content, inbound.SentAt, inbound.ProfileName, now);
        db.Set<Message>().Add(message);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>Records the status; <see langword="false"/> for a duplicate or a message the platform did not send.</summary>
    public async Task<bool> RecordStatusAsync(WhatsAppStatusReport status, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(status);
        var metaId = status.MetaMessageId.Trim();
        var message = await db.Set<Message>()
            .Include(m => m.StatusChanges)
            .FirstOrDefaultAsync(m => m.MetaMessageId == metaId && m.Direction == MessageDirection.Outbound, cancellationToken);
        if (message is null)
        {
            LogUnknownMessage(status.Status.ToString());
            return false;
        }

        if (!message.RecordStatus(status.Status, status.OccurredAt, status.ErrorCode, status.ErrorTitle, status.Pricing, clock.GetUtcNow()))
        {
            return false;
        }

        if (status.Status == MessageStatus.Failed)
        {
            LogDeliveryFailed(status.ErrorCode, status.ErrorTitle ?? string.Empty);
        }

        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>
    /// First contact: links the person of this phone, when exactly one person has it (relatives sharing a number are left
    /// for the agent to choose, ADR-0027), with its individual payer when it exists.
    /// </summary>
    private async Task LinkKnownPersonAsync(Conversation conversation, CancellationToken cancellationToken)
    {
        var persons = await customers.FindPersonsByPhoneAsync(conversation.WhatsAppNumber, cancellationToken);
        if (persons.Count != 1)
        {
            return;
        }

        var personId = persons[0].Id;
        var customer = await customers.FindIndividualCustomerAsync(personId, cancellationToken);
        conversation.LinkPerson(personId, customer?.Id);
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "WhatsApp message already recorded (duplicate delivery): ignored.")]
    private partial void LogDuplicateMessage();

    [LoggerMessage(Level = LogLevel.Warning, Message = "WhatsApp message from an invalid sender number: ignored.")]
    private partial void LogInvalidSender();

    [LoggerMessage(Level = LogLevel.Warning, Message = "WhatsApp message with a blank or over-long Meta id: ignored.")]
    private partial void LogInvalidMetaId();

    [LoggerMessage(Level = LogLevel.Debug, Message = "WhatsApp status {Status} for a message the platform did not send: ignored.")]
    private partial void LogUnknownMessage(string status);

    [LoggerMessage(Level = LogLevel.Warning, Message = "WhatsApp message not delivered: Meta error {ErrorCode} ({ErrorTitle}).")]
    private partial void LogDeliveryFailed(int? errorCode, string errorTitle);
}
