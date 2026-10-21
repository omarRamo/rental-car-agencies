using CarRental.Application.Messaging.Contracts;
using CarRental.Domain.Common;
using CarRental.Domain.Messaging;

namespace CarRental.Application.Messaging;

/// <summary>Conversions between the domain types of the module and its contract types (same enum values).</summary>
internal static class MessagingMapping
{
    public static ConversationModeInfo ToInfo(this ConversationMode mode) => (ConversationModeInfo)(int)mode;

    public static MessageDirectionInfo ToInfo(this MessageDirection direction) => (MessageDirectionInfo)(int)direction;

    public static MessageKindInfo ToInfo(this MessageKind kind) => (MessageKindInfo)(int)kind;

    public static MessageAuthorInfo ToInfo(this MessageAuthor author) => (MessageAuthorInfo)(int)author;

    public static MessageStatusInfo ToInfo(this MessageStatus status) => (MessageStatusInfo)(int)status;

    public static MediaKindInfo ToInfo(this MediaKind kind) => (MediaKindInfo)(int)kind;

    public static GearboxPreferenceInfo ToInfo(this GearboxPreference gearbox) => (GearboxPreferenceInfo)(int)gearbox;

    public static GearboxPreference ToDomain(this GearboxPreferenceInfo gearbox) => (GearboxPreference)(int)gearbox;

    public static ConversationNeedInfo ToInfo(this ConversationNeed need)
    {
        ArgumentNullException.ThrowIfNull(need);
        return new ConversationNeedInfo(
            need.PickupPlace,
            need.ReturnPlace,
            need.PickupAt,
            need.ReturnAt,
            need.Passengers,
            need.Luggage,
            need.Gearbox?.ToInfo(),
            need.Budget);
    }

    public static Result<ConversationNeed> ToDomain(this ConversationNeedInfo need)
    {
        ArgumentNullException.ThrowIfNull(need);
        return ConversationNeed.Create(
            need.PickupPlace,
            need.ReturnPlace,
            need.PickupAt,
            need.ReturnAt,
            need.Passengers,
            need.Luggage,
            need.Gearbox?.ToDomain(),
            need.Budget);
    }

    public static ConversationUsageInfo ToInfo(this ConversationUsage usage)
    {
        ArgumentNullException.ThrowIfNull(usage);
        return new ConversationUsageInfo(
            usage.BotTurns,
            usage.InputTokens,
            usage.OutputTokens,
            usage.CacheReadTokens,
            usage.CacheWriteTokens,
            usage.ToolCalls,
            usage.AiCostUsd,
            usage.MetaCostUsd);
    }

    /// <summary>The usage of a turn (counts and costs below zero are clamped: they are bugs of the responder, not data).</summary>
    public static BotTurnUsage ToDomain(this ConversationTurnUsageInfo usage)
    {
        ArgumentNullException.ThrowIfNull(usage);
        return new BotTurnUsage(
            Math.Max(0, usage.InputTokens),
            Math.Max(0, usage.OutputTokens),
            Math.Max(0, usage.CacheReadTokens),
            Math.Max(0, usage.CacheWriteTokens),
            Math.Max(0, usage.ToolCalls),
            Math.Max(0m, usage.AiCostUsd),
            Math.Max(0m, usage.MetaCostUsd));
    }

    public static ConversationInfo ToInfo(this Conversation conversation)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        return new ConversationInfo(
            conversation.Id,
            conversation.WhatsAppNumber,
            conversation.ProfileName,
            conversation.PersonId,
            conversation.CustomerId,
            conversation.BookingId,
            conversation.Mode.ToInfo(),
            conversation.Status == ConversationStatus.Open,
            conversation.LastActivityAtUtc,
            conversation.LastCustomerMessageAtUtc,
            conversation.WindowClosesAtUtc,
            conversation.Need.ToInfo(),
            conversation.Usage.ToInfo());
    }

    public static ConversationMessageInfo ToInfo(this Message message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return new ConversationMessageInfo(
            message.Id,
            message.Direction.ToInfo(),
            message.Author.ToInfo(),
            message.Kind.ToInfo(),
            message.Text,
            (message.Content as ReplyContent)?.ReplyId,
            (message.Content as MediaContent)?.MediaKind.ToInfo(),
            (message.Content as TemplateContent)?.Name,
            message.Status.ToInfo(),
            message.OccurredAtUtc);
    }

    /// <summary>The domain content of a message to send, validated against the limits of the Cloud API.</summary>
    public static Result<MessageContent> ToDomain(this OutboundMessageContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        return content switch
        {
            OutboundText text => Upcast(TextContent.Create(text.Body)),
            OutboundButtons buttons => ButtonsOf(buttons),
            OutboundList list => ListOf(list),
            OutboundTemplate template => Upcast(TemplateContent.Create(template.Name, template.Language, [.. template.Parameters ?? []])),
            _ => MessagingErrors.ContentNotAllowed,
        };
    }

    private static Result<MessageContent> ButtonsOf(OutboundButtons content)
    {
        var buttons = new List<ReplyButton>(content.Buttons?.Count ?? 0);
        foreach (var button in content.Buttons ?? [])
        {
            if (button is null)
            {
                return MessagingErrors.ButtonsInvalid;
            }

            var created = ReplyButton.Create(button.Id, button.Title);
            if (created.IsFailure)
            {
                return created.Error;
            }

            buttons.Add(created.Value);
        }

        return Upcast(ButtonsContent.Create(content.Body, buttons, content.Header, content.Footer));
    }

    private static Result<MessageContent> ListOf(OutboundList content)
    {
        var sections = new List<ListSection>(content.Sections?.Count ?? 0);
        foreach (var section in content.Sections ?? [])
        {
            if (section is null)
            {
                return MessagingErrors.ListInvalid;
            }

            var rows = new List<ListRow>(section.Rows?.Count ?? 0);
            foreach (var row in section.Rows ?? [])
            {
                if (row is null)
                {
                    return MessagingErrors.ListInvalid;
                }

                var created = ListRow.Create(row.Id, row.Title, row.Description);
                if (created.IsFailure)
                {
                    return created.Error;
                }

                rows.Add(created.Value);
            }

            var built = ListSection.Create(section.Title, rows);
            if (built.IsFailure)
            {
                return built.Error;
            }

            sections.Add(built.Value);
        }

        return Upcast(ListContent.Create(content.Body, content.ButtonText, sections, content.Header, content.Footer));
    }

    private static Result<MessageContent> Upcast<TContent>(Result<TContent> result)
        where TContent : MessageContent =>
        result.IsSuccess ? Result.Success<MessageContent>(result.Value) : Result.Failure<MessageContent>(result.Error);
}
