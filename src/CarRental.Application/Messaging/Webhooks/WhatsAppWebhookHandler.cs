using CarRental.Application.Common.Outbox;
using Microsoft.Extensions.Logging;

namespace CarRental.Application.Messaging.Webhooks;

/// <summary>
/// Processes a stored webhook delivery: parses it, then records each customer message and each status in a scope of its
/// own (<see cref="WhatsAppEventRecorder"/>). The customer messages belong to the agency served by the platform's number
/// (<see cref="WhatsAppAgencyRouting"/>) and are recorded in a scope working for it; when no agency serves the number
/// (none configured, ambiguous, suspended or with its WhatsApp module off) they are recorded nowhere, and a warning says
/// so. A status updates the message sent with its Meta id, whatever its agency. Redelivery is harmless: messages are
/// deduplicated by Meta id and statuses by message and status, so a failure part-way retries the whole delivery through
/// the outbox without duplicates.
/// </summary>
internal sealed partial class WhatsAppWebhookHandler(
    IWhatsAppWebhookParser parser,
    WhatsAppAgencyRouting routing,
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
        if (batch.Messages.Count > 0)
        {
            var route = await routing.ResolveAsync(cancellationToken);
            if (route.AgencyId is not { } agencyId)
            {
                LogNoAgency(batch.Messages.Count, route.Status.ToString());
            }
            else
            {
                foreach (var inbound in batch.Messages)
                {
                    if (await scopes.RunAsync<WhatsAppEventRecorder, bool>(
                            (recorder, ct) => recorder.RecordMessageAsync(agencyId, inbound, ct),
                            context.CorrelationId,
                            cancellationToken,
                            agencyId))
                    {
                        recorded++;
                    }
                }
            }
        }

        // Meta ids are unique on the platform: a status finds its message across agencies.
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "WhatsApp webhook: {Messages} customer message(s) recorded nowhere, no agency is served by the platform number ({Reason}; WhatsApp:AgencyId or WhatsApp:AgencySlug, and the WhatsApp module of the agency).")]
    private partial void LogNoAgency(int messages, string reason);

    [LoggerMessage(Level = LogLevel.Debug, Message = "WhatsApp webhook: {Messages} message(s), {Statuses} status(es), {Ignored} ignored, {Recorded} recorded.")]
    private partial void LogProcessed(int messages, int statuses, int ignored, int recorded);
}
