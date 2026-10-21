using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Messaging.Contracts;

namespace CarRental.Application.Messaging.Inbox;

/// <summary>Which conversations the shared inbox lists.</summary>
public enum InboxView
{
    /// <summary>Open conversations (default).</summary>
    Open = 1,

    /// <summary>Open conversations with customer messages not read by an agent.</summary>
    Unread = 2,

    /// <summary>Open conversations handled by an agent, or handed over by the bot and waiting for one.</summary>
    Human = 3,

    /// <summary>Open conversations answered by the bot.</summary>
    Bot = 4,

    /// <summary>Closed conversations.</summary>
    Closed = 5,

    /// <summary>Every conversation.</summary>
    All = 6,
}

/// <summary>Filter of the conversation list: the view and a search on the number or the profile name.</summary>
public sealed record InboxFilter(InboxView View = InboxView.Open, string? Search = null);

/// <summary>A line of the conversation list.</summary>
/// <param name="Id">The conversation.</param>
/// <param name="WhatsAppNumber">Number in E.164.</param>
/// <param name="ProfileName">WhatsApp profile name.</param>
/// <param name="PersonId">Linked person.</param>
/// <param name="PersonName">Name of the linked person (empty when anonymized).</param>
/// <param name="BookingId">Linked rental file.</param>
/// <param name="BookingNumber">Number of the linked file.</param>
/// <param name="Mode">Bot or human.</param>
/// <param name="IsOpen">Not closed.</param>
/// <param name="AwaitsAgent">Handed over by the bot, no agent has taken it yet.</param>
/// <param name="HumanAgentName">The agent who took it over.</param>
/// <param name="UnreadCount">Customer messages not read by an agent.</param>
/// <param name="LastActivityAtUtc">Last message, either way.</param>
/// <param name="WindowClosesAtUtc">End of the 24-hour window (null when the customer never wrote).</param>
/// <param name="LastMessage">The last message (preview).</param>
public sealed record InboxConversationItem(
    Guid Id,
    string WhatsAppNumber,
    string? ProfileName,
    Guid? PersonId,
    string? PersonName,
    Guid? BookingId,
    string? BookingNumber,
    ConversationModeInfo Mode,
    bool IsOpen,
    bool AwaitsAgent,
    string? HumanAgentName,
    int UnreadCount,
    DateTimeOffset LastActivityAtUtc,
    DateTimeOffset? WindowClosesAtUtc,
    InboxMessagePreview? LastMessage);

/// <summary>Preview of the last message of a conversation (its text cut to <see cref="MaxLength"/> characters).</summary>
public sealed record InboxMessagePreview(MessageDirectionInfo Direction, MessageAuthorInfo Author, MessageKindInfo Kind, string? Text, MessageStatusInfo Status)
{
    public const int MaxLength = 90;
}

/// <summary>A conversation opened in the shared inbox: its header, links, need, bot usage and thread.</summary>
public sealed record InboxConversationDetails(
    Guid Id,
    string WhatsAppNumber,
    string? ProfileName,
    ConversationModeInfo Mode,
    bool IsOpen,
    DateTimeOffset? HumanSinceUtc,
    Guid? HumanAgentId,
    string? HumanAgentName,
    string? HandoverReason,
    int UnreadCount,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset LastActivityAtUtc,
    DateTimeOffset? LastCustomerMessageAtUtc,
    DateTimeOffset? WindowClosesAtUtc,
    bool IsWindowOpen,
    InboxLinkedPerson? Person,
    IReadOnlyList<InboxPersonCandidate> PersonCandidates,
    InboxLinkedBooking? Booking,
    IReadOnlyList<InboxLinkedBooking> SuggestedBookings,
    ConversationNeedInfo Need,
    ConversationUsageInfo Usage,
    IReadOnlyList<InboxMessage> Messages,
    bool HasEarlierMessages);

/// <summary>The person linked to a conversation and whether it accepts WhatsApp notifications.</summary>
public sealed record InboxLinkedPerson(Guid Id, string FullName, string? Phone, bool IsAnonymized, bool NotificationsConsent);

/// <summary>A person whose phone is the number of the conversation (relatives may share a number).</summary>
public sealed record InboxPersonCandidate(Guid Id, string FullName, DateOnly? BirthDate);

/// <summary>A rental file linked to (or suggested for) a conversation.</summary>
public sealed record InboxLinkedBooking(Guid Id, string Number, BookingStatusInfo Status, DateTimeOffset PickupAt, DateTimeOffset ReturnAt);

/// <summary>A message of the thread, with its delivery details.</summary>
/// <param name="Id">The message.</param>
/// <param name="Direction">Inbound or outbound.</param>
/// <param name="Author">Customer, bot, agent or system.</param>
/// <param name="AuthorName">The agent who wrote it.</param>
/// <param name="Kind">Type of the message.</param>
/// <param name="Text">Readable text (body, chosen reply, template parameters).</param>
/// <param name="Choices">Buttons or rows offered by an interactive message (titles).</param>
/// <param name="ListButton">Text of the button of a list.</param>
/// <param name="ReplyId">Id of the choice of a reply.</param>
/// <param name="MediaKind">Kind of a media (never shown).</param>
/// <param name="UnsupportedType">Original type of an unsupported message.</param>
/// <param name="TemplateName">Name of a template.</param>
/// <param name="TemplateLanguage">Language of a template.</param>
/// <param name="Status">Current delivery state.</param>
/// <param name="OccurredAtUtc">When it was sent (customer) or queued (platform).</param>
/// <param name="DeliveredAtUtc">Delivered to the phone.</param>
/// <param name="ReadAtUtc">Read by the customer.</param>
/// <param name="FailureCode">Why it failed or was not sent (<c>messaging.*</c>).</param>
/// <param name="MetaErrorCode">Error code of Meta.</param>
/// <param name="MetaErrorTitle">Error title of Meta.</param>
/// <param name="PricingCategory">Pricing category reported by Meta.</param>
/// <param name="IsBillable">Billable according to Meta.</param>
public sealed record InboxMessage(
    Guid Id,
    MessageDirectionInfo Direction,
    MessageAuthorInfo Author,
    string? AuthorName,
    MessageKindInfo Kind,
    string? Text,
    IReadOnlyList<string> Choices,
    string? ListButton,
    string? ReplyId,
    MediaKindInfo? MediaKind,
    string? UnsupportedType,
    string? TemplateName,
    string? TemplateLanguage,
    MessageStatusInfo Status,
    DateTimeOffset OccurredAtUtc,
    DateTimeOffset? DeliveredAtUtc,
    DateTimeOffset? ReadAtUtc,
    string? FailureCode,
    int? MetaErrorCode,
    string? MetaErrorTitle,
    string? PricingCategory,
    bool? IsBillable);

/// <summary>State of the inbox: whether messages can be sent (WhatsApp configured) and the templates an agent can send.</summary>
public sealed record InboxOverview(bool IsEnabled, IReadOnlyList<InboxTemplate> Templates);

/// <summary>An approved template an agent can send, with the number of its body parameters.</summary>
public sealed record InboxTemplate(InboxTemplateKind Kind, string Name, string Language, int ParameterCount);

/// <summary>The templates of the platform (their names are settings, their parameters fixed by ADR-0048).</summary>
public enum InboxTemplateKind
{
    /// <summary>Reopens a conversation outside the 24-hour window: {{1}} customer name.</summary>
    FollowUp = 1,

    BookingConfirmed = 2,
    PaymentLink = 3,
    PickupReminder = 4,
    ReturnReminder = 5,
    ReviewRequest = 6,
}
