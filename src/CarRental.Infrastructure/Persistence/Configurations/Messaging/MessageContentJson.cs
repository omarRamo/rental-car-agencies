using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using CarRental.Domain.Common;
using CarRental.Domain.Messaging;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace CarRental.Infrastructure.Persistence.Configurations.Messaging;

/// <summary>
/// Storage format of a <see cref="MessageContent"/>: a small JSON object with a <c>kind</c> discriminator (the
/// platform's own format, not Meta's), read back through the factories of the domain. Contents never change once
/// created, so the comparer compares the JSON texts.
/// </summary>
internal static class MessageContentJson
{
    private static readonly JsonWriterOptions WriterOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static ValueConverter<MessageContent, string> Converter { get; } = new(content => Serialize(content), json => Deserialize(json));

    public static ValueComparer<MessageContent> Comparer { get; } = new(
        (left, right) => ReferenceEquals(left, right) || (left != null && right != null && Serialize(left) == Serialize(right)),
        content => Serialize(content).GetHashCode(StringComparison.Ordinal),
        content => content);

    public static string Serialize(MessageContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStartObject();
            switch (content)
            {
                case TextContent text:
                    writer.WriteString("kind", "text");
                    writer.WriteString("body", text.Body);
                    break;
                case ButtonsContent buttons:
                    writer.WriteString("kind", "buttons");
                    WriteInteractive(writer, buttons.Body, buttons.Header, buttons.Footer);
                    writer.WriteStartArray("buttons");
                    foreach (var button in buttons.Buttons)
                    {
                        writer.WriteStartObject();
                        writer.WriteString("id", button.Id);
                        writer.WriteString("title", button.Title);
                        writer.WriteEndObject();
                    }

                    writer.WriteEndArray();
                    break;
                case ListContent list:
                    writer.WriteString("kind", "list");
                    WriteInteractive(writer, list.Body, list.Header, list.Footer);
                    writer.WriteString("button", list.ButtonText);
                    writer.WriteStartArray("sections");
                    foreach (var section in list.Sections)
                    {
                        writer.WriteStartObject();
                        writer.WriteString("title", section.Title);
                        writer.WriteStartArray("rows");
                        foreach (var row in section.Rows)
                        {
                            writer.WriteStartObject();
                            writer.WriteString("id", row.Id);
                            writer.WriteString("title", row.Title);
                            writer.WriteString("description", row.Description);
                            writer.WriteEndObject();
                        }

                        writer.WriteEndArray();
                        writer.WriteEndObject();
                    }

                    writer.WriteEndArray();
                    break;
                case TemplateContent template:
                    writer.WriteString("kind", "template");
                    writer.WriteString("name", template.Name);
                    writer.WriteString("language", template.Language);
                    writer.WriteStartArray("parameters");
                    foreach (var parameter in template.Parameters)
                    {
                        writer.WriteStringValue(parameter);
                    }

                    writer.WriteEndArray();
                    break;
                case ReplyContent reply:
                    writer.WriteString("kind", reply.Kind == MessageKind.ListReply ? "list_reply" : "button_reply");
                    writer.WriteString("id", reply.ReplyId);
                    writer.WriteString("title", reply.Title);
                    writer.WriteString("description", reply.Description);
                    break;
                case MediaContent media:
                    writer.WriteString("kind", "media");
                    writer.WriteString("media", media.MediaKind.ToString());
                    writer.WriteString("id", media.MediaId);
                    break;
                case UnsupportedContent unsupported:
                    writer.WriteString("kind", "unsupported");
                    writer.WriteString("type", unsupported.OriginalType);
                    break;
                default:
                    throw new InvalidOperationException($"No storage format for the message content {content.GetType().Name}.");
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    public static MessageContent Deserialize(string json)
    {
        ArgumentException.ThrowIfNullOrEmpty(json);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var kind = Text(root, "kind");
        return kind switch
        {
            "text" => Valid(TextContent.Create(Text(root, "body"))),
            "buttons" => Valid(ButtonsContent.Create(
                Text(root, "body"),
                [.. Array(root, "buttons").Select(b => Valid(ReplyButton.Create(Text(b, "id"), Text(b, "title"))))],
                Text(root, "header"),
                Text(root, "footer"))),
            "list" => Valid(ListContent.Create(
                Text(root, "body"),
                Text(root, "button"),
                [
                    .. Array(root, "sections").Select(s => Valid(ListSection.Create(
                        Text(s, "title"),
                        [.. Array(s, "rows").Select(r => Valid(ListRow.Create(Text(r, "id"), Text(r, "title"), Text(r, "description"))))]))),
                ],
                Text(root, "header"),
                Text(root, "footer"))),
            "template" => Valid(TemplateContent.Create(Text(root, "name"), Text(root, "language"), [.. Array(root, "parameters").Select(p => p.GetString())])),
            "button_reply" => ReplyContent.ButtonChoice(Text(root, "id"), Text(root, "title")),
            "list_reply" => ReplyContent.ListChoice(Text(root, "id"), Text(root, "title"), Text(root, "description")),
            "media" => MediaContent.Received(Enum.Parse<MediaKind>(Text(root, "media") ?? nameof(MediaKind.Document)), Text(root, "id")),
            "unsupported" => UnsupportedContent.Received(Text(root, "type")),
            _ => throw new InvalidOperationException($"Unknown stored message content kind '{kind}'."),
        };
    }

    private static void WriteInteractive(Utf8JsonWriter writer, string body, string? header, string? footer)
    {
        writer.WriteString("body", body);
        writer.WriteString("header", header);
        writer.WriteString("footer", footer);
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static JsonElement[] Array(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array ? [.. value.EnumerateArray()] : [];

    /// <summary>A stored content was valid when written: failing to read it back is a bug or a corruption.</summary>
    private static T Valid<T>(Result<T> result) =>
        result.IsSuccess ? result.Value : throw new InvalidOperationException($"Stored message content is invalid ({result.Error.Code}).");
}
