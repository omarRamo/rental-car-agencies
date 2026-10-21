using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace CarRental.Infrastructure.Messaging;

/// <summary>
/// Webhook payloads shaped like Meta's (WhatsApp Cloud API documentation): used by <see cref="FakeWhatsApp"/> to
/// simulate customers and delivery statuses, and by the tests to post signed deliveries.
/// </summary>
public static class MetaWebhookPayloads
{
    private static readonly JsonWriterOptions WriterOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>A text message from <paramref name="from"/> (a <c>wa_id</c>).</summary>
    public static string TextMessage(
        string businessAccountId,
        string phoneNumberId,
        string from,
        string? profileName,
        string messageId,
        DateTimeOffset sentAt,
        string body) =>
        Message(businessAccountId, phoneNumberId, from, profileName, messageId, sentAt, "text", writer =>
        {
            writer.WriteStartObject("text");
            writer.WriteString("body", body);
            writer.WriteEndObject();
        });

    /// <summary>The choice of a reply button of an interactive message.</summary>
    public static string ButtonReply(
        string businessAccountId,
        string phoneNumberId,
        string from,
        string? profileName,
        string messageId,
        DateTimeOffset sentAt,
        string buttonId,
        string title) =>
        Message(businessAccountId, phoneNumberId, from, profileName, messageId, sentAt, "interactive", writer =>
        {
            writer.WriteStartObject("interactive");
            writer.WriteString("type", "button_reply");
            writer.WriteStartObject("button_reply");
            writer.WriteString("id", buttonId);
            writer.WriteString("title", title);
            writer.WriteEndObject();
            writer.WriteEndObject();
        });

    /// <summary>An image (only its Meta id travels; the platform never downloads it).</summary>
    public static string Image(string businessAccountId, string phoneNumberId, string from, string? profileName, string messageId, DateTimeOffset sentAt, string mediaId) =>
        Message(businessAccountId, phoneNumberId, from, profileName, messageId, sentAt, "image", writer =>
        {
            writer.WriteStartObject("image");
            writer.WriteString("mime_type", "image/jpeg");
            writer.WriteString("id", mediaId);
            writer.WriteEndObject();
        });

    /// <summary>
    /// A delivery status (<c>sent</c>, <c>delivered</c>, <c>read</c> or <c>failed</c> with an error) of a message sent to
    /// <paramref name="recipientId"/>, with the pricing object of the per-message pricing model.
    /// </summary>
    public static string Status(
        string businessAccountId,
        string phoneNumberId,
        string messageId,
        string recipientId,
        string status,
        DateTimeOffset at,
        string pricingCategory = "service",
        bool billable = true,
        int? errorCode = null,
        string? errorTitle = null) =>
        Envelope(businessAccountId, phoneNumberId, writer =>
        {
            writer.WriteStartArray("statuses");
            writer.WriteStartObject();
            writer.WriteString("id", messageId);
            writer.WriteString("status", status);
            writer.WriteString("timestamp", Unix(at));
            writer.WriteString("recipient_id", recipientId);
            writer.WriteStartObject("pricing");
            writer.WriteBoolean("billable", billable);
            writer.WriteString("pricing_model", "PMP");
            writer.WriteString("category", pricingCategory);
            writer.WriteEndObject();
            if (errorCode is { } code)
            {
                writer.WriteStartArray("errors");
                writer.WriteStartObject();
                writer.WriteNumber("code", code);
                writer.WriteString("title", errorTitle ?? "Error");
                writer.WriteEndObject();
                writer.WriteEndArray();
            }

            writer.WriteEndObject();
            writer.WriteEndArray();
        });

    private static string Message(
        string businessAccountId,
        string phoneNumberId,
        string from,
        string? profileName,
        string messageId,
        DateTimeOffset sentAt,
        string type,
        Action<Utf8JsonWriter> writeContent) =>
        Envelope(businessAccountId, phoneNumberId, writer =>
        {
            writer.WriteStartArray("contacts");
            writer.WriteStartObject();
            writer.WriteStartObject("profile");
            writer.WriteString("name", profileName ?? string.Empty);
            writer.WriteEndObject();
            writer.WriteString("wa_id", from);
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteStartArray("messages");
            writer.WriteStartObject();
            writer.WriteString("from", from);
            writer.WriteString("id", messageId);
            writer.WriteString("timestamp", Unix(sentAt));
            writer.WriteString("type", type);
            writeContent(writer);
            writer.WriteEndObject();
            writer.WriteEndArray();
        });

    private static string Envelope(string businessAccountId, string phoneNumberId, Action<Utf8JsonWriter> writeValue)
    {
        var buffer = new ArrayBufferWriter<byte>(512);
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("object", "whatsapp_business_account");
            writer.WriteStartArray("entry");
            writer.WriteStartObject();
            writer.WriteString("id", businessAccountId);
            writer.WriteStartArray("changes");
            writer.WriteStartObject();
            writer.WriteString("field", "messages");
            writer.WriteStartObject("value");
            writer.WriteString("messaging_product", "whatsapp");
            writer.WriteStartObject("metadata");
            writer.WriteString("display_phone_number", "21600000000");
            writer.WriteString("phone_number_id", phoneNumberId);
            writer.WriteEndObject();
            writeValue(writer);
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static string Unix(DateTimeOffset instant) => instant.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
}
