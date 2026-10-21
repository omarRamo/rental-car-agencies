using CarRental.Application.Common.Outbox;
using Microsoft.Extensions.Logging;

namespace CarRental.Application.Messaging.Webhooks;

/// <summary>
/// Processes a stored webhook delivery: parses it, then records each customer message and each status in a scope of its
/// own (<see cref="WhatsAppEventRecorder"/>). Redelivery is harmless: messages are deduplicated by Meta id and statuses
/// by message and status, so a failure part-way retries the whole delivery through the outbox without duplicates.
/// </summary>
internal sealed partial class WhatsAppWebhookHandler(
    IWhatsAppWebhookParser parser,
    MessagingScopes scopes,
    ILogger<WhatsAppWebhookHandler> logger) : IOutboxHandler<WhatsAppWebhookReceived>
{
    public async Task HandleAsync(WhatsAppWebhookReceived message, OutboxMessageContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(context);
        var parsed = parser.Parse(message.Payload);
        if (parsed.IsFailure)
        {
            // Signed by Meta but unreadable: retrying would not help; the delivery is kept as processed for analysis.
            LogUnreadable(context.MessageId, parsed.Error.Code);
            return;
        }

        var batch = parsed.Value;
        var recorded = 0;
        foreach (var inbound in batch.Messages)
        {
            if (await scopes.RunAsync<WhatsAppEventRecorder, bool>((recorder, ct) => recorder.RecordMessageAsync(inbound, ct), context.CorrelationId, cancellationToken))
            {
                recorded++;
            }
        }

        foreach (var status in batch.Statuses)
        {
            if (await scopes.RunAsync<WhatsAppEventRecorder, bool>((recorder, ct) => recorder.RecordStatusAsync(status, ct), context.CorrelationId, cancellationToken))
            {
                recorded++;
            }
        }

        LogProcessed(batch.Messages.Count, batch.Statuses.Count, batch.Ignored, recorded);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "WhatsApp webhook delivery {OutboxMessageId} is not readable ({Reason}): ignored.")]
    private partial void LogUnreadable(Guid outboxMessageId, string reason);

    [LoggerMessage(Level = LogLevel.Debug, Message = "WhatsApp webhook: {Messages} message(s), {Statuses} status(es), {Ignored} ignored, {Recorded} recorded.")]
    private partial void LogProcessed(int messages, int statuses, int ignored, int recorded);
}
