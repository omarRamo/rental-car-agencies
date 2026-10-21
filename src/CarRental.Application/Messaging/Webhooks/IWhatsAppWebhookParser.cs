using CarRental.Domain.Common;
using CarRental.Domain.Messaging;

namespace CarRental.Application.Messaging.Webhooks;

/// <summary>
/// Reads a webhook payload of the WhatsApp Cloud API (<c>object</c> = <c>whatsapp_business_account</c>, field
/// <c>messages</c>): the customer messages and the statuses of the messages sent, for the configured business account and
/// phone number. Everything else (other fields, other numbers, unknown shapes) is counted as ignored. Implemented by the
/// infrastructure, which owns the Meta formats.
/// </summary>
public interface IWhatsAppWebhookParser
{
    /// <summary>The events of <paramref name="payload"/>, or an error when it is not JSON of the expected envelope.</summary>
    Result<WhatsAppWebhookBatch> Parse(string payload);
}

/// <summary>The events of one webhook delivery, in the order of the payload.</summary>
public sealed record WhatsAppWebhookBatch(IReadOnlyList<InboundWhatsAppMessage> Messages, IReadOnlyList<WhatsAppStatusReport> Statuses, int Ignored)
{
    public static WhatsAppWebhookBatch Empty { get; } = new([], [], 0);
}

/// <summary>A customer message: Meta id (deduplication key), sender <c>wa_id</c>, profile name, time it was sent and content.</summary>
public sealed record InboundWhatsAppMessage(string MetaMessageId, string From, string? ProfileName, DateTimeOffset SentAt, MessageContent Content);

/// <summary>
/// A delivery status of a message sent by the platform (<see cref="MessageStatus.Sent"/>, <see cref="MessageStatus.Delivered"/>,
/// <see cref="MessageStatus.Read"/> or <see cref="MessageStatus.Failed"/> with the Meta error) and its pricing information.
/// </summary>
public sealed record WhatsAppStatusReport(
    string MetaMessageId,
    string RecipientId,
    MessageStatus Status,
    DateTimeOffset OccurredAt,
    int? ErrorCode,
    string? ErrorTitle,
    MessagePricing? Pricing);
