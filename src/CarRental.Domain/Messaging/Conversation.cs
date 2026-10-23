using CarRental.Domain.Common;

namespace CarRental.Domain.Messaging;

/// <summary>
/// The WhatsApp conversation of an agency with one customer number (one per agency and number, closed and reopened over
/// time; a customer may write to two agencies): who answers (the bot, or an agent who took it over), the 24-hour
/// customer service window opened by the customer's last message, the optional links to a person, a payer and a rental
/// file, the structured need understood by the bot and what the bot cost so far. Every message is created here, with the
/// agency of the conversation, so that the rules of Meta and of the platform always hold:
/// <list type="bullet">
/// <item>a free-form message (text, interactive) is only sent within 24 hours of the customer's last message; outside,
/// only an approved template;</item>
/// <item>a conversation in <see cref="ConversationMode.Human"/> mode never gets a bot reply, and an agent answers only
/// after taking the conversation over.</item>
/// </list>
/// </summary>
public sealed class Conversation : AggregateRoot, IAgencyOwned
{
    public const int ProfileNameMaxLength = 100;
    public const int HandoverReasonMaxLength = 500;
    public const int AgentNameMaxLength = AuditActor.UserNameMaxLength;

    /// <summary>Meta's customer service window: free-form messages are allowed for 24 hours after the customer's last message.</summary>
    public static readonly TimeSpan CustomerServiceWindow = TimeSpan.FromHours(24);

    private Conversation()
    {
    }

    private Conversation(Guid id, Guid agencyId, string whatsAppNumber, string? profileName, DateTimeOffset now)
        : base(id)
    {
        AgencyId = agencyId;
        WhatsAppNumber = whatsAppNumber;
        ProfileName = profileName;
        Mode = ConversationMode.Bot;
        Status = ConversationStatus.Open;
        CreatedAtUtc = now;
        LastActivityAtUtc = now;
        Need = ConversationNeed.Empty;
        Usage = ConversationUsage.Zero;
    }

    /// <summary>The agency the customer writes to (the agency served by the WhatsApp number of the platform).</summary>
    public Guid AgencyId { get; private set; }

    /// <summary>The customer's WhatsApp number in E.164 (<c>+21698123456</c>), unique within the agency.</summary>
    public string WhatsAppNumber { get; private set; } = null!;

    /// <summary>The WhatsApp profile name of the customer, as reported by Meta.</summary>
    public string? ProfileName { get; private set; }

    /// <summary>The person (Customers module) this number belongs to, when known.</summary>
    public Guid? PersonId { get; private set; }

    /// <summary>The payer (Customers module) concerned, when known.</summary>
    public Guid? CustomerId { get; private set; }

    /// <summary>The rental file (Bookings module) the conversation is about, when known.</summary>
    public Guid? BookingId { get; private set; }

    public ConversationMode Mode { get; private set; }

    /// <summary>Since when an agent (or the bot's hand-over) put the conversation in <see cref="ConversationMode.Human"/> mode.</summary>
    public DateTimeOffset? HumanSinceUtc { get; private set; }

    /// <summary>The agent who took the conversation over; <see langword="null"/> after a hand-over by the bot, until an agent takes it.</summary>
    public Guid? HumanAgentId { get; private set; }

    public string? HumanAgentName { get; private set; }

    /// <summary>Why the bot handed the conversation over to a human.</summary>
    public string? HandoverReason { get; private set; }

    public ConversationStatus Status { get; private set; }

    public DateTimeOffset? ClosedAtUtc { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    /// <summary>Last message in either direction (order of the inbox, retention).</summary>
    public DateTimeOffset LastActivityAtUtc { get; private set; }

    /// <summary>Last message of the customer: opens the 24-hour customer service window.</summary>
    public DateTimeOffset? LastCustomerMessageAtUtc { get; private set; }

    /// <summary>Customer messages not read by an agent yet.</summary>
    public int UnreadCount { get; private set; }

    public DateTimeOffset? LastReadAtUtc { get; private set; }

    public ConversationNeed Need { get; private set; } = null!;

    public ConversationUsage Usage { get; private set; } = null!;

    /// <summary>End of the customer service window, <see langword="null"/> when the customer never wrote.</summary>
    public DateTimeOffset? WindowClosesAtUtc => LastCustomerMessageAtUtc + CustomerServiceWindow;

    /// <summary>The conversation of <paramref name="agencyId"/> with a customer number (the agency always comes from the platform, never from the customer).</summary>
    public static Result<Conversation> Start(Guid agencyId, string? whatsAppNumber, string? profileName, DateTimeOffset now)
    {
        if (agencyId == Guid.Empty)
        {
            throw new ArgumentException("A conversation belongs to an agency.", nameof(agencyId));
        }

        var number = WhatsAppNumbers.Normalize(whatsAppNumber);
        if (number.IsFailure)
        {
            return number.Error;
        }

        var conversation = new Conversation(NewId(), agencyId, number.Value, CleanProfileName(profileName), now);
        conversation.Raise(new ConversationStarted(conversation.Id));
        return conversation;
    }

    /// <summary>Whether a free-form message can still be sent at <paramref name="now"/>.</summary>
    public bool IsWindowOpen(DateTimeOffset now) => WindowClosesAtUtc is { } closes && now < closes;

    /// <summary>
    /// A message of the customer (Meta id required). It opens (or extends) the 24-hour window from the time the customer
    /// sent it, reopens a closed conversation, and — in <see cref="ConversationMode.Bot"/> mode — asks for one turn of the
    /// auto-responder (<see cref="CustomerMessageReceived"/>).
    /// </summary>
    public Message ReceiveCustomerMessage(string metaMessageId, MessageContent content, DateTimeOffset sentAt, string? profileName, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(metaMessageId);
        ArgumentNullException.ThrowIfNull(content);
        if (content.Kind is MessageKind.Interactive or MessageKind.Template)
        {
            throw new ArgumentException("A customer sends text, replies, media or unsupported messages.", nameof(content));
        }

        // A clock ahead of ours never extends the window beyond 24 hours from now.
        var at = sentAt.ToUniversalTime() > now ? now : sentAt.ToUniversalTime();
        if (LastCustomerMessageAtUtc is null || at > LastCustomerMessageAtUtc)
        {
            LastCustomerMessageAtUtc = at;
        }

        if (at > LastActivityAtUtc)
        {
            LastActivityAtUtc = at;
        }

        UnreadCount++;
        if (Status == ConversationStatus.Closed)
        {
            Status = ConversationStatus.Open;
            ClosedAtUtc = null;
        }

        if (CleanProfileName(profileName) is { } name)
        {
            ProfileName = name;
        }

        var botTurn = Mode == ConversationMode.Bot;
        var message = Message.Inbound(AgencyId, Id, MessageTexts.Truncate(metaMessageId.Trim(), Message.MetaMessageIdMaxLength), content, at, botTurn, now);
        if (botTurn)
        {
            Raise(new CustomerMessageReceived(Id, message.Id));
        }

        return message;
    }

    /// <summary>The auto-responder's reply of a turn: text or interactive, in bot mode, within the window.</summary>
    public Result<Message> ReplyAsBot(MessageContent content, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (Mode != ConversationMode.Bot)
        {
            return MessagingErrors.BotSilenced;
        }

        if (!content.IsFreeForm)
        {
            return MessagingErrors.ContentNotAllowed;
        }

        if (!IsWindowOpen(now))
        {
            return MessagingErrors.WindowClosed;
        }

        LastActivityAtUtc = now;
        return Message.Outbound(AgencyId, Id, MessageAuthor.Bot, content, agent: null, now);
    }

    /// <summary>
    /// An agent's message from the shared inbox, by the agent who took the conversation over (a colleague takes it over
    /// in turn; after a hand-over by the bot, an agent takes it first): free-form within the window, an approved template
    /// at any time. The customer's messages count as read.
    /// </summary>
    public Result<Message> ReplyAsAgent(MessageContent content, AuditActor agent, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(agent);
        if (Mode != ConversationMode.Human || agent.UserId is not { } agentId || HumanAgentId != agentId)
        {
            return MessagingErrors.NotTakenOver;
        }

        if (!content.IsFreeForm && content is not TemplateContent)
        {
            return MessagingErrors.ContentNotAllowed;
        }

        if (content.IsFreeForm && !IsWindowOpen(now))
        {
            return MessagingErrors.WindowClosed;
        }

        if (Status == ConversationStatus.Closed)
        {
            Status = ConversationStatus.Open;
            ClosedAtUtc = null;
        }

        MarkRead(now);
        LastActivityAtUtc = now;
        return Message.Outbound(AgencyId, Id, MessageAuthor.Agent, content, agent, now);
    }

    /// <summary>A booking notification of the platform: an approved template, whatever the mode and the window.</summary>
    public Message Notify(TemplateContent template, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(template);
        LastActivityAtUtc = now;
        return Message.Outbound(AgencyId, Id, MessageAuthor.System, template, agent: null, now);
    }

    /// <summary>An agent takes the conversation over: the bot is silenced. Taking over from another agent reassigns it.</summary>
    public Result TakeOver(AuditActor agent, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(agent);
        if (agent.UserId is not { } agentId)
        {
            throw new ArgumentException("Only a user of the back office takes a conversation over.", nameof(agent));
        }

        if (Mode == ConversationMode.Human && HumanAgentId == agentId)
        {
            return Result.Success();
        }

        if (Mode == ConversationMode.Bot)
        {
            HumanSinceUtc = now;
            HandoverReason = null;
        }

        Mode = ConversationMode.Human;
        HumanAgentId = agentId;
        HumanAgentName = agent.UserName is { } name ? MessageTexts.Truncate(name, AgentNameMaxLength) : null;
        Raise(new ConversationModeChanged(Id, Mode, agentId));
        return Result.Success();
    }

    /// <summary>
    /// The auto-responder hands the conversation over to a human (complaint, complex case, caps reached…): the bot is
    /// silenced until an agent hands it back. Only from bot mode.
    /// </summary>
    public Result HandOverToHuman(string? reason, DateTimeOffset now)
    {
        var cleaned = MessageTexts.SingleLine(reason);
        if (cleaned is null || cleaned.Length > HandoverReasonMaxLength)
        {
            return MessagingErrors.HandoverReasonInvalid;
        }

        if (Mode != ConversationMode.Bot)
        {
            return MessagingErrors.BotSilenced;
        }

        Mode = ConversationMode.Human;
        HumanSinceUtc = now;
        HumanAgentId = null;
        HumanAgentName = null;
        HandoverReason = cleaned;
        Raise(new ConversationModeChanged(Id, Mode, AgentId: null));
        return Result.Success();
    }

    /// <summary>An agent hands the conversation back to the bot, which answers the next customer message.</summary>
    public Result HandBackToBot(DateTimeOffset now)
    {
        if (Mode == ConversationMode.Bot)
        {
            return Result.Success();
        }

        Mode = ConversationMode.Bot;
        HumanSinceUtc = null;
        HumanAgentId = null;
        HumanAgentName = null;
        HandoverReason = null;
        Raise(new ConversationModeChanged(Id, Mode, AgentId: null));
        return Result.Success();
    }

    /// <summary>Adds the usage of one turn of the auto-responder to the counters.</summary>
    public void RecordBotTurn(BotTurnUsage usage)
    {
        ArgumentNullException.ThrowIfNull(usage);
        Usage = Usage.Add(usage);
    }

    /// <summary>Replaces the structured need understood by the bot.</summary>
    public void UpdateNeed(ConversationNeed need)
    {
        ArgumentNullException.ThrowIfNull(need);
        Need = need;
    }

    /// <summary>Links the conversation to a person and its payer, or unlinks it (<see langword="null"/>).</summary>
    public void LinkPerson(Guid? personId, Guid? customerId)
    {
        if (personId == Guid.Empty || customerId == Guid.Empty)
        {
            throw new ArgumentException("A link is an id or none.", nameof(personId));
        }

        PersonId = personId;
        CustomerId = personId is null ? null : customerId;
    }

    /// <summary>Links the conversation to a rental file, or unlinks it (<see langword="null"/>).</summary>
    public void LinkBooking(Guid? bookingId)
    {
        if (bookingId == Guid.Empty)
        {
            throw new ArgumentException("A link is an id or none.", nameof(bookingId));
        }

        BookingId = bookingId;
    }

    /// <summary>An agent read the customer's messages.</summary>
    public void MarkRead(DateTimeOffset now)
    {
        UnreadCount = 0;
        LastReadAtUtc = now;
    }

    /// <summary>The case is handled; the next customer message reopens the conversation (the mode is kept).</summary>
    public void Close(DateTimeOffset now)
    {
        if (Status == ConversationStatus.Closed)
        {
            return;
        }

        Status = ConversationStatus.Closed;
        ClosedAtUtc = now;
        MarkRead(now);
    }

    public void Reopen()
    {
        Status = ConversationStatus.Open;
        ClosedAtUtc = null;
    }

    private static string? CleanProfileName(string? profileName) =>
        MessageTexts.SingleLine(profileName) is { } name ? MessageTexts.Truncate(name, ProfileNameMaxLength) : null;
}
