using System.Globalization;
using System.Text.Json;
using CarRental.Application.Messaging.Webhooks;
using CarRental.Domain.Common;
using CarRental.Domain.Messaging;
using Microsoft.Extensions.Options;

namespace CarRental.Infrastructure.Messaging;

/// <summary>
/// Reads the webhook payloads of the WhatsApp Cloud API, as documented by Meta: <c>object</c> =
/// <c>whatsapp_business_account</c>, <c>entry[].id</c> (business account), <c>entry[].changes[]</c> with
/// <c>field</c> = <c>messages</c> and a <c>value</c> holding <c>metadata.phone_number_id</c>, <c>contacts[]</c>
/// (<c>wa_id</c>, <c>profile.name</c>), <c>messages[]</c> (<c>from</c>, <c>id</c>, <c>timestamp</c> in Unix seconds,
/// <c>type</c> and its object) and <c>statuses[]</c> (<c>id</c>, <c>status</c>, <c>timestamp</c>, <c>recipient_id</c>,
/// <c>errors[]</c>, <c>pricing</c>). Entries of another business account or phone number are ignored.
/// </summary>
/// <remarks>
/// Message types: <c>text</c>; <c>interactive</c> (<c>button_reply</c>, <c>list_reply</c>); <c>button</c> (quick reply
/// of a template: <c>payload</c>, <c>text</c>); <c>image</c>, <c>audio</c>, <c>video</c>, <c>document</c>,
/// <c>sticker</c> (only the media id is kept); anything else (<c>location</c>, <c>contacts</c>, <c>reaction</c>,
/// <c>system</c>, <c>unsupported</c>…) is kept as unsupported with its type.
/// </remarks>
internal sealed class WhatsAppWebhookParser(IOptions<WhatsAppOptions> options) : IWhatsAppWebhookParser
{
    private static readonly Error Unreadable = MessagingErrors.WebhookUnreadable;

    public Result<WhatsAppWebhookBatch> Parse(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return Unreadable;
        }

        try
        {
            using var document = JsonDocument.Parse(payload);
            return Read(document.RootElement);
        }
        catch (JsonException)
        {
            return Unreadable;
        }
    }

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static JsonElement Child(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : default;

    private static JsonElement[] Items(JsonElement element, string name) =>
        Child(element, name) is { ValueKind: JsonValueKind.Array } array ? [.. array.EnumerateArray()] : [];

    /// <summary>A Unix timestamp in seconds (Meta sends it as a string).</summary>
    private static DateTimeOffset? Timestamp(JsonElement element)
    {
        var value = Child(element, "timestamp");
        long seconds;
        if (value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
        {
            seconds = parsed;
        }
        else if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
        {
            seconds = number;
        }
        else
        {
            return null;
        }

        return seconds is > 0 and < 32_503_680_000 ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null;
    }

    private static MessageContent ContentOf(JsonElement message, string type)
    {
        switch (type)
        {
            case "text":
                return TextContent.Received(Text(Child(message, "text"), "body"));
            case "interactive":
                {
                    var interactive = Child(message, "interactive");
                    switch (Text(interactive, "type"))
                    {
                        case "button_reply":
                            {
                                var reply = Child(interactive, "button_reply");
                                return ReplyContent.ButtonChoice(Text(reply, "id"), Text(reply, "title"));
                            }

                        case "list_reply":
                            {
                                var reply = Child(interactive, "list_reply");
                                return ReplyContent.ListChoice(Text(reply, "id"), Text(reply, "title"), Text(reply, "description"));
                            }

                        default:
                            return UnsupportedContent.Received("interactive");
                    }
                }

            case "button":
                {
                    var button = Child(message, "button");
                    return ReplyContent.ButtonChoice(Text(button, "payload"), Text(button, "text"));
                }

            case "image":
                return MediaContent.Received(MediaKind.Image, Text(Child(message, type), "id"));
            case "audio":
                return MediaContent.Received(MediaKind.Audio, Text(Child(message, type), "id"));
            case "video":
                return MediaContent.Received(MediaKind.Video, Text(Child(message, type), "id"));
            case "document":
                return MediaContent.Received(MediaKind.Document, Text(Child(message, type), "id"));
            case "sticker":
                return MediaContent.Received(MediaKind.Sticker, Text(Child(message, type), "id"));
            default:
                return UnsupportedContent.Received(type);
        }
    }

    private static MessageStatus? StatusOf(string? status) => status switch
    {
        "sent" => MessageStatus.Sent,
        "delivered" => MessageStatus.Delivered,
        "read" => MessageStatus.Read,
        "failed" => MessageStatus.Failed,
        _ => null,
    };

    private static MessagePricing? PricingOf(JsonElement status)
    {
        var pricing = Child(status, "pricing");
        if (pricing.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var billable = Child(pricing, "billable") is { ValueKind: JsonValueKind.True or JsonValueKind.False } flag ? flag.GetBoolean() : (bool?)null;
        return new MessagePricing(Text(pricing, "category"), billable, Text(pricing, "pricing_model"));
    }

    private Result<WhatsAppWebhookBatch> Read(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || Child(root, "entry") is not { ValueKind: JsonValueKind.Array })
        {
            return Unreadable;
        }

        var settings = options.Value;
        var messages = new List<InboundWhatsAppMessage>();
        var statuses = new List<WhatsAppStatusReport>();
        var ignored = 0;
        if (Text(root, "object") != "whatsapp_business_account")
        {
            return new WhatsAppWebhookBatch([], [], Items(root, "entry").Length);
        }

        foreach (var entry in Items(root, "entry"))
        {
            if (!string.IsNullOrEmpty(settings.BusinessAccountId) && Text(entry, "id") != settings.BusinessAccountId)
            {
                ignored++;
                continue;
            }

            foreach (var change in Items(entry, "changes"))
            {
                var value = Child(change, "value");
                if (Text(change, "field") != "messages" || value.ValueKind != JsonValueKind.Object
                    || (!string.IsNullOrEmpty(settings.PhoneNumberId) && Text(Child(value, "metadata"), "phone_number_id") != settings.PhoneNumberId))
                {
                    ignored++;
                    continue;
                }

                var names = Items(value, "contacts")
                    .Select(contact => (WaId: Text(contact, "wa_id"), Name: Text(Child(contact, "profile"), "name")))
                    .Where(contact => contact.WaId is not null)
                    .GroupBy(contact => contact.WaId!, StringComparer.Ordinal)
                    .ToDictionary(group => group.Key, group => group.First().Name, StringComparer.Ordinal);

                foreach (var message in Items(value, "messages"))
                {
                    var id = Text(message, "id");
                    var from = Text(message, "from");
                    var type = Text(message, "type");
                    if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(type) || Timestamp(message) is not { } sentAt)
                    {
                        ignored++;
                        continue;
                    }

                    messages.Add(new InboundWhatsAppMessage(id, from, names.GetValueOrDefault(from), sentAt, ContentOf(message, type)));
                }

                foreach (var status in Items(value, "statuses"))
                {
                    var id = Text(status, "id");
                    if (string.IsNullOrWhiteSpace(id) || StatusOf(Text(status, "status")) is not { } reported || Timestamp(status) is not { } at)
                    {
                        ignored++;
                        continue;
                    }

                    var error = Items(status, "errors").FirstOrDefault();
                    int? errorCode = Child(error, "code") is { ValueKind: JsonValueKind.Number } code && code.TryGetInt32(out var number) ? number : null;
                    statuses.Add(new WhatsAppStatusReport(id, Text(status, "recipient_id") ?? string.Empty, reported, at, errorCode, Text(error, "title"), PricingOf(status)));
                }
            }
        }

        return new WhatsAppWebhookBatch(messages, statuses, ignored);
    }
}
