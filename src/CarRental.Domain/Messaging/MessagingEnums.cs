namespace CarRental.Domain.Messaging;

/// <summary>Who answers the customer: the bot (auto-responder) or a human agent who took the conversation over.</summary>
public enum ConversationMode
{
    /// <summary>The auto-responder answers each customer turn (at most one message per turn).</summary>
    Bot = 1,

    /// <summary>An agent took over (or the bot handed over): the bot never answers in this mode.</summary>
    Human = 2,
}

/// <summary>Whether the conversation is being handled; a new customer message reopens a closed one.</summary>
public enum ConversationStatus
{
    Open = 1,
    Closed = 2,
}

public enum MessageDirection
{
    /// <summary>Sent by the customer, received through the webhook.</summary>
    Inbound = 1,

    /// <summary>Sent by the platform (bot, agent or notification) through the Graph API.</summary>
    Outbound = 2,
}

/// <summary>Type of a message, as stored (SPEC §13 "Message").</summary>
public enum MessageKind
{
    Text = 1,

    /// <summary>Outbound interactive message: reply buttons or a list.</summary>
    Interactive = 2,

    /// <summary>Inbound choice of a reply button (interactive message) or of a quick reply button (template).</summary>
    ButtonReply = 3,

    /// <summary>Inbound choice of a row of an interactive list.</summary>
    ListReply = 4,

    /// <summary>Outbound approved template (the only message allowed outside the 24-hour window).</summary>
    Template = 5,

    /// <summary>Inbound image, audio, video, document or sticker: never downloaded, only its type and Meta id are kept.</summary>
    Media = 6,

    /// <summary>Any other inbound type (location, contacts, reaction…): only the original type is kept.</summary>
    Unsupported = 7,
}

/// <summary>Who wrote a message.</summary>
public enum MessageAuthor
{
    Customer = 1,

    /// <summary>The auto-responder (package 4B).</summary>
    Bot = 2,

    /// <summary>An agent of the back office (shared inbox).</summary>
    Agent = 3,

    /// <summary>The platform: booking notifications.</summary>
    System = 4,
}

/// <summary>
/// Delivery state of a message. Inbound messages are <see cref="Received"/>. Outbound ones are <see cref="Queued"/>
/// until the Graph API accepts them (<see cref="Accepted"/>), then follow the statuses reported by Meta (sent,
/// delivered, read, failed); <see cref="Cancelled"/> means never sent (the bot was silenced, the window closed).
/// </summary>
public enum MessageStatus
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

/// <summary>What the auto-responder did with an inbound customer message (one turn per message at most).</summary>
public enum BotTurnState
{
    /// <summary>Not a bot turn: an outbound message, or a customer message received in <see cref="ConversationMode.Human"/> mode.</summary>
    None = 0,

    /// <summary>Waiting for the responder.</summary>
    Pending = 1,

    /// <summary>The responder produced the reply message.</summary>
    Replied = 2,

    /// <summary>The responder ran and chose not to reply.</summary>
    NoReply = 3,

    /// <summary>A newer customer message arrived before the turn ran: that turn answers both.</summary>
    Superseded = 4,

    /// <summary>An agent took over before the turn ran: the bot stays silent.</summary>
    Silenced = 5,
}

/// <summary>Kind of an inbound media (WhatsApp Cloud API message types).</summary>
public enum MediaKind
{
    Image = 1,
    Audio = 2,
    Video = 3,
    Document = 4,
    Sticker = 5,
}

/// <summary>Gearbox wished by the customer (structured need of the bot).</summary>
public enum GearboxPreference
{
    Manual = 1,
    Automatic = 2,
}

/// <summary>
/// Utility notification about a rental file, sent with an approved template and only with the WhatsApp consent of the
/// customer (SPEC §11 "Notifications sortantes"). At most one per file and kind.
/// </summary>
public enum NotificationKind
{
    /// <summary>The file was confirmed.</summary>
    BookingConfirmed = 1,

    /// <summary>A validated request waits for its payment (fallback payment mode, <c>AwaitingPayment</c>).</summary>
    PaymentLink = 2,

    /// <summary>The day before the pick-up, with the address.</summary>
    PickupReminder = 3,

    /// <summary>The day before the return.</summary>
    ReturnReminder = 4,

    /// <summary>After the return: a review request.</summary>
    ReviewRequest = 5,
}
