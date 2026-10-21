using CarRental.Domain.Common;

namespace CarRental.Application.Messaging.Contracts;

/// <summary>Who answers the customer (same values as the domain enum).</summary>
public enum ConversationModeInfo
{
    /// <summary>The auto-responder answers each customer turn.</summary>
    Bot = 1,

    /// <summary>An agent handles the conversation: the auto-responder is never called.</summary>
    Human = 2,
}

/// <summary>Direction of a message (same values as the domain enum).</summary>
public enum MessageDirectionInfo
{
    Inbound = 1,
    Outbound = 2,
}

/// <summary>Type of a message (same values as the domain enum).</summary>
public enum MessageKindInfo
{
    Text = 1,
    Interactive = 2,
    ButtonReply = 3,
    ListReply = 4,
    Template = 5,
    Media = 6,
    Unsupported = 7,
}

/// <summary>Author of a message (same values as the domain enum).</summary>
public enum MessageAuthorInfo
{
    Customer = 1,
    Bot = 2,
    Agent = 3,
    System = 4,
}

/// <summary>Delivery state of a message (same values as the domain enum).</summary>
public enum MessageStatusInfo
{
    Received = 1,
    Queued = 2,
    Accepted = 3,
    Sent = 4,
    Delivered = 5,
    Read = 6,
    Failed = 7,
    Cancelled = 8,
}

/// <summary>Kind of an inbound media (same values as the domain enum).</summary>
public enum MediaKindInfo
{
    Image = 1,
    Audio = 2,
    Video = 3,
    Document = 4,
    Sticker = 5,
}

/// <summary>Gearbox wished by the customer (same values as the domain enum).</summary>
public enum GearboxPreferenceInfo
{
    Manual = 1,
    Automatic = 2,
}

/// <summary>
/// The structured need understood by the bot (every field optional): places (location code such as <c>TUN</c> or the
/// customer's words), wished instants (UTC), passengers, bags, gearbox and budget of the stay in TND.
/// </summary>
public sealed record ConversationNeedInfo(
    string? PickupPlace = null,
    string? ReturnPlace = null,
    DateTimeOffset? PickupAt = null,
    DateTimeOffset? ReturnAt = null,
    int? Passengers = null,
    int? Luggage = null,
    GearboxPreferenceInfo? Gearbox = null,
    Money? Budget = null)
{
    /// <summary>Nothing known yet.</summary>
    public static ConversationNeedInfo Empty { get; } = new();
}

/// <summary>What the auto-responder cost so far on a conversation (turns, tokens, tool calls, AI and estimated Meta costs in USD).</summary>
public sealed record ConversationUsageInfo(
    int BotTurns,
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long CacheWriteTokens,
    int ToolCalls,
    decimal AiCostUsd,
    decimal MetaCostUsd);

/// <summary>
/// A message of a conversation, as the auto-responder and the other modules see it: <see cref="Text"/> is the text body,
/// the body of an interactive message, the title of a chosen reply or the parameters of a template; a chosen reply
/// carries its <see cref="ReplyId"/>; a media only its kind (its content is never downloaded nor shown).
/// </summary>
public sealed record ConversationMessageInfo(
    Guid Id,
    MessageDirectionInfo Direction,
    MessageAuthorInfo Author,
    MessageKindInfo Kind,
    string? Text,
    string? ReplyId,
    MediaKindInfo? MediaKind,
    string? TemplateName,
    MessageStatusInfo Status,
    DateTimeOffset OccurredAtUtc);

/// <summary>
/// A WhatsApp conversation: the customer's number (E.164) and profile name, the links to a person, payer and rental file
/// (ids of the Customers and Bookings modules), the mode, the end of the 24-hour window, the need and the usage of the bot.
/// </summary>
public sealed record ConversationInfo(
    Guid Id,
    string WhatsAppNumber,
    string? ProfileName,
    Guid? PersonId,
    Guid? CustomerId,
    Guid? BookingId,
    ConversationModeInfo Mode,
    bool IsOpen,
    DateTimeOffset LastActivityAtUtc,
    DateTimeOffset? LastCustomerMessageAtUtc,
    DateTimeOffset? WindowClosesAtUtc,
    ConversationNeedInfo Need,
    ConversationUsageInfo Usage);

/// <summary>
/// The content of a message the platform sends: a text, reply buttons, a list (free-form, within the 24-hour window only)
/// or an approved template. Limits are those of the WhatsApp Cloud API: text 4096 characters; buttons: body 1024, 1 to 3
/// buttons with unique ids (256) and titles (20); list: body 4096, button text 20, 1 to 10 rows with unique ids (200),
/// titles (24) and descriptions (72), sections titled (24) when there are several; header and footer 60.
/// </summary>
public abstract record OutboundMessageContent
{
    private protected OutboundMessageContent()
    {
    }
}

/// <summary>A text message.</summary>
public sealed record OutboundText(string Body) : OutboundMessageContent;

/// <summary>An interactive message with 1 to 3 reply buttons; the id of the chosen button comes back as a reply.</summary>
public sealed record OutboundButtons(string Body, IReadOnlyList<ReplyButtonInfo> Buttons, string? Header = null, string? Footer = null)
    : OutboundMessageContent;

/// <summary>An interactive list of up to 10 rows, opened by a button; the id of the chosen row comes back as a reply.</summary>
public sealed record OutboundList(string Body, string ButtonText, IReadOnlyList<ListSectionInfo> Sections, string? Header = null, string? Footer = null)
    : OutboundMessageContent;

/// <summary>An approved template: name and language as approved by Meta, positional body parameters.</summary>
public sealed record OutboundTemplate(string Name, string Language, IReadOnlyList<string> Parameters) : OutboundMessageContent;

/// <summary>A reply button.</summary>
public sealed record ReplyButtonInfo(string Id, string Title);

/// <summary>A section of a list (its title is required when the list has several sections).</summary>
public sealed record ListSectionInfo(string? Title, IReadOnlyList<ListRowInfo> Rows);

/// <summary>A row of a list.</summary>
public sealed record ListRowInfo(string Id, string Title, string? Description = null);
