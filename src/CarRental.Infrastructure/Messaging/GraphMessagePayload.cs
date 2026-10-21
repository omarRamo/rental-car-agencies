using System.Buffers;
using System.Text.Encodings.Web;
using System.Text.Json;
using CarRental.Domain.Messaging;

namespace CarRental.Infrastructure.Messaging;

/// <summary>
/// Body of <c>POST /{phone-number-id}/messages</c> (WhatsApp Cloud API, as documented by Meta) for each content the
/// platform sends: <c>text</c> (<c>preview_url</c> off), <c>interactive</c> of type <c>button</c> (reply buttons) or
/// <c>list</c>, and <c>template</c> (name, <c>language.code</c>, body parameters of type <c>text</c>). Header texts are
/// sent as <c>{"type":"text","text":…}</c>.
/// </summary>
public static class GraphMessagePayload
{
    private static readonly JsonWriterOptions WriterOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static byte[] Create(string to, MessageContent content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(to);
        ArgumentNullException.ThrowIfNull(content);
        var buffer = new ArrayBufferWriter<byte>(512);
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("messaging_product", "whatsapp");
            writer.WriteString("recipient_type", "individual");
            writer.WriteString("to", to);
            switch (content)
            {
                case TextContent text:
                    writer.WriteString("type", "text");
                    writer.WriteStartObject("text");
                    writer.WriteBoolean("preview_url", false);
                    writer.WriteString("body", text.Body);
                    writer.WriteEndObject();
                    break;
                case ButtonsContent buttons:
                    writer.WriteString("type", "interactive");
                    writer.WriteStartObject("interactive");
                    writer.WriteString("type", "button");
                    WriteTexts(writer, buttons.Header, buttons.Body, buttons.Footer);
                    writer.WriteStartObject("action");
                    writer.WriteStartArray("buttons");
                    foreach (var button in buttons.Buttons)
                    {
                        writer.WriteStartObject();
                        writer.WriteString("type", "reply");
                        writer.WriteStartObject("reply");
                        writer.WriteString("id", button.Id);
                        writer.WriteString("title", button.Title);
                        writer.WriteEndObject();
                        writer.WriteEndObject();
                    }

                    writer.WriteEndArray();
                    writer.WriteEndObject();
                    writer.WriteEndObject();
                    break;
                case ListContent list:
                    writer.WriteString("type", "interactive");
                    writer.WriteStartObject("interactive");
                    writer.WriteString("type", "list");
                    WriteTexts(writer, list.Header, list.Body, list.Footer);
                    writer.WriteStartObject("action");
                    writer.WriteString("button", list.ButtonText);
                    writer.WriteStartArray("sections");
                    foreach (var section in list.Sections)
                    {
                        writer.WriteStartObject();
                        if (section.Title is not null)
                        {
                            writer.WriteString("title", section.Title);
                        }

                        writer.WriteStartArray("rows");
                        foreach (var row in section.Rows)
                        {
                            writer.WriteStartObject();
                            writer.WriteString("id", row.Id);
                            writer.WriteString("title", row.Title);
                            if (row.Description is not null)
                            {
                                writer.WriteString("description", row.Description);
                            }

                            writer.WriteEndObject();
                        }

                        writer.WriteEndArray();
                        writer.WriteEndObject();
                    }

                    writer.WriteEndArray();
                    writer.WriteEndObject();
                    writer.WriteEndObject();
                    break;
                case TemplateContent template:
                    writer.WriteString("type", "template");
                    writer.WriteStartObject("template");
                    writer.WriteString("name", template.Name);
                    writer.WriteStartObject("language");
                    writer.WriteString("code", template.Language);
                    writer.WriteEndObject();
                    if (template.Parameters.Count > 0)
                    {
                        writer.WriteStartArray("components");
                        writer.WriteStartObject();
                        writer.WriteString("type", "body");
                        writer.WriteStartArray("parameters");
                        foreach (var parameter in template.Parameters)
                        {
                            writer.WriteStartObject();
                            writer.WriteString("type", "text");
                            writer.WriteString("text", parameter);
                            writer.WriteEndObject();
                        }

                        writer.WriteEndArray();
                        writer.WriteEndObject();
                        writer.WriteEndArray();
                    }

                    writer.WriteEndObject();
                    break;
                default:
                    throw new ArgumentException($"The platform does not send {content.Kind} messages.", nameof(content));
            }

            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    private static void WriteTexts(Utf8JsonWriter writer, string? header, string body, string? footer)
    {
        if (header is not null)
        {
            writer.WriteStartObject("header");
            writer.WriteString("type", "text");
            writer.WriteString("text", header);
            writer.WriteEndObject();
        }

        writer.WriteStartObject("body");
        writer.WriteString("text", body);
        writer.WriteEndObject();
        if (footer is not null)
        {
            writer.WriteStartObject("footer");
            writer.WriteString("text", footer);
            writer.WriteEndObject();
        }
    }
}
