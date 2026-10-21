using System.Text.RegularExpressions;
using CarRental.Application.Messaging.Contracts;
using Microsoft.Extensions.AI;

namespace CarRental.Application.Messaging.Agent;

/// <summary>
/// The conversation as the model sees it (data minimisation, ADR-0056): the customer's messages as user turns, the
/// bot's replies as assistant turns, an agent's message or a notification announced as such (no template parameter: they
/// carry names), a media as « the customer sent an image — not opened », a chosen button as its title and id. Messages
/// never sent (cancelled, failed) are left out; consecutive messages of one side are merged so that the turns
/// alternate, and the transcript starts with the customer. The same message is always rendered the same way, so that a
/// conversation keeps its prefix from one turn to the next. The number, the profile name and the linked persons never
/// reach the model.
/// </summary>
internal static partial class AgentTranscript
{
    /// <summary>The markup of the platform context: a customer cannot forge it (removed from their texts).</summary>
    public const string ContextTag = "contexte_plateforme";

    public const int HistoryTextMaxLength = 1_000;
    public const int NewTextMaxLength = 2_000;

    /// <summary>The system prompt, then the history and the customer messages of the turn (the last turn is the customer's).</summary>
    public static List<ChatMessage> Build(string systemPrompt, ConversationTurn turn)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(systemPrompt);
        ArgumentNullException.ThrowIfNull(turn);
        var turns = new List<(ChatRole Role, List<string> Texts)>();
        foreach (var message in turn.History)
        {
            if (Render(message, HistoryTextMaxLength) is { } rendered)
            {
                Append(turns, RoleOf(message), rendered);
            }
        }

        foreach (var message in turn.NewMessages)
        {
            Append(turns, ChatRole.User, Render(message, NewTextMaxLength) ?? "[message vide]");
        }

        // The transcript starts with the customer: earlier notifications and agent messages carry nothing the model needs.
        while (turns.Count > 0 && turns[0].Role != ChatRole.User)
        {
            turns.RemoveAt(0);
        }

        var messages = new List<ChatMessage> { new(ChatRole.System, systemPrompt) };
        messages.AddRange(turns.Select(t => new ChatMessage(t.Role, [.. t.Texts.Select(text => (AIContent)new TextContent(text))])));
        return messages;
    }

    /// <summary>How one message reads for the model; <see langword="null"/> for a message to leave out.</summary>
    public static string? Render(ConversationMessageInfo message, int maxLength)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.Status is MessageStatusInfo.Cancelled or MessageStatusInfo.Failed)
        {
            return null;
        }

        if (message.Direction == MessageDirectionInfo.Inbound)
        {
            return message.Kind switch
            {
                MessageKindInfo.Media => $"[Le client a envoyé {MediaName(message.MediaKind)} : pièce jointe non ouverte (jamais traitée sur WhatsApp).]",
                MessageKindInfo.Unsupported => "[Le client a envoyé un message d'un type non pris en charge (non lu).]",
                MessageKindInfo.ButtonReply or MessageKindInfo.ListReply =>
                    $"[Choix du client : « {Clean(message.Text, 100)} »{(message.ReplyId is { } id ? $" (id {Clean(id, 60)})" : string.Empty)}]",
                _ => Clean(message.Text, maxLength) is { Length: > 0 } text ? text : null,
            };
        }

        var body = Clean(message.Text, maxLength);
        return message.Author switch
        {
            MessageAuthorInfo.Bot => body is { Length: > 0 } ? body : null,
            MessageAuthorInfo.Agent => body is { Length: > 0 } ? $"[Message d'un agent de l'agence] {body}" : null,
            MessageAuthorInfo.System => $"[Notification automatique envoyée au client : {message.TemplateName ?? "modèle"}]",
            _ => null,
        };
    }

    public static bool IsAttachment(ConversationMessageInfo message) =>
        message.Kind is MessageKindInfo.Media or MessageKindInfo.Unsupported;

    private static ChatRole RoleOf(ConversationMessageInfo message) =>
        message.Direction == MessageDirectionInfo.Inbound ? ChatRole.User : ChatRole.Assistant;

    private static void Append(List<(ChatRole Role, List<string> Texts)> turns, ChatRole role, string text)
    {
        if (turns.Count > 0 && turns[^1].Role == role)
        {
            turns[^1].Texts.Add(text);
        }
        else
        {
            turns.Add((role, [text]));
        }
    }

    private static string MediaName(MediaKindInfo? kind) => kind switch
    {
        MediaKindInfo.Image => "une image",
        MediaKindInfo.Audio => "un message audio",
        MediaKindInfo.Video => "une vidéo",
        MediaKindInfo.Document => "un document",
        MediaKindInfo.Sticker => "un autocollant",
        _ => "une pièce jointe",
    };

    /// <summary>The text cut to <paramref name="maxLength"/>, without the platform markup a customer could try to forge.</summary>
    private static string Clean(string? text, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var cleaned = ContextTagPattern().Replace(text.Trim(), "(balise retirée)");
        return cleaned.Length <= maxLength ? cleaned : cleaned[..maxLength] + "…";
    }

    [GeneratedRegex(@"</?\s*contexte_plateforme[^>]*>?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ContextTagPattern();
}
