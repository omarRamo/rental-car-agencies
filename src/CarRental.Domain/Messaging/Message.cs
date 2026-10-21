using CarRental.Domain.Common;

namespace CarRental.Domain.Messaging;

/// <summary>
/// A WhatsApp message of a <see cref="Conversation"/>, inbound or outbound (SPEC §14 "chaque message WhatsApp, envoyé ou
/// reçu, avec son statut de livraison"). Only the conversation creates messages (it enforces the 24-hour window and the
/// bot/human mode); a message is then loaded on its own — a thread may count hundreds of them — and only its delivery
/// state changes: acceptance by the Graph API, then the statuses reported by Meta (sent, delivered, read, failed) with
/// their error code and the pricing category, each status kept once (duplicate webhooks are ignored).
/// </summary>
public sealed class Message : AggregateRoot
{
    public const int MetaMessageIdMaxLength = 256;
    public const int AuthorNameMaxLength = AuditActor.UserNameMaxLength;
    public const int FailureCodeMaxLength = 100;
    public const int MetaErrorTitleMaxLength = 256;
    public const int PricingFieldMaxLength = 40;

    private readonly List<MessageStatusChange> _statusChanges = [];

    private Message()
    {
    }

    private Message(
        Guid id,
        Guid conversationId,
        MessageDirection direction,
        MessageAuthor author,
        MessageContent content,
        MessageStatus status,
        DateTimeOffset occurredAt,
        DateTimeOffset now)
        : base(id)
    {
        ConversationId = conversationId;
        Direction = direction;
        Author = author;
        Content = content;
        Kind = content.Kind;
        Text = content.Text is { } text ? MessageTexts.Truncate(text, TextContent.MaxLength) : null;
        Status = status;
        OccurredAtUtc = occurredAt.ToUniversalTime();
        CreatedAtUtc = now;
    }

    public Guid ConversationId { get; private set; }

    public MessageDirection Direction { get; private set; }

    public MessageAuthor Author { get; private set; }

    /// <summary>The agent who wrote an outbound message from the shared inbox.</summary>
    public Guid? AuthorUserId { get; private set; }

    public string? AuthorName { get; private set; }

    public MessageKind Kind { get; private set; }

    public MessageContent Content { get; private set; } = null!;

    /// <summary>Readable text of the content (<see cref="MessageContent.Text"/>), kept in a column for the lists.</summary>
    public string? Text { get; private set; }

    /// <summary>Id of the message at Meta (<c>wamid.…</c>): the deduplication key of inbound messages and of statuses.</summary>
    public string? MetaMessageId { get; private set; }

    public MessageStatus Status { get; private set; }

    /// <summary>When the customer sent it (inbound, Meta timestamp) or when it was queued (outbound).</summary>
    public DateTimeOffset OccurredAtUtc { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset? AcceptedAtUtc { get; private set; }

    public DateTimeOffset? SentAtUtc { get; private set; }

    public DateTimeOffset? DeliveredAtUtc { get; private set; }

    public DateTimeOffset? ReadAtUtc { get; private set; }

    public DateTimeOffset? FailedAtUtc { get; private set; }

    /// <summary>Why the message failed or was not sent: a <c>messaging.*</c> error code (texts in the web resources).</summary>
    public string? FailureCode { get; private set; }

    /// <summary>Error code reported by Meta (Graph API answer or failed status), e.g. 131047.</summary>
    public int? MetaErrorCode { get; private set; }

    public string? MetaErrorTitle { get; private set; }

    /// <summary>Pricing category reported by Meta in the statuses (<c>service</c>, <c>utility</c>, <c>marketing</c>…).</summary>
    public string? PricingCategory { get; private set; }

    /// <summary>Whether Meta reported the message as billable.</summary>
    public bool? IsBillable { get; private set; }

    /// <summary>Pricing model reported by Meta (e.g. <c>PMP</c>).</summary>
    public string? PricingModel { get; private set; }

    /// <summary>For an inbound customer message: what the auto-responder did with it.</summary>
    public BotTurnState BotTurn { get; private set; }

    public IReadOnlyList<MessageStatusChange> StatusChanges => _statusChanges;

    public bool IsInbound => Direction == MessageDirection.Inbound;

    /// <summary>A customer message (Meta id required), received at <paramref name="sentAt"/>.</summary>
    internal static Message Inbound(Guid conversationId, string metaMessageId, MessageContent content, DateTimeOffset sentAt, bool botTurn, DateTimeOffset now) =>
        new(NewId(), conversationId, MessageDirection.Inbound, MessageAuthor.Customer, content, MessageStatus.Received, sentAt, now)
        {
            MetaMessageId = metaMessageId,
            BotTurn = botTurn ? BotTurnState.Pending : BotTurnState.None,
        };

    /// <summary>An outbound message, queued for the outbox (which sends it through the Graph API).</summary>
    internal static Message Outbound(Guid conversationId, MessageAuthor author, MessageContent content, AuditActor? agent, DateTimeOffset now)
    {
        var message = new Message(NewId(), conversationId, MessageDirection.Outbound, author, content, MessageStatus.Queued, now, now)
        {
            AuthorUserId = agent?.UserId,
            AuthorName = agent?.UserName is { } name ? MessageTexts.Truncate(name, AuthorNameMaxLength) : null,
        };
        message.Raise(new OutboundMessageQueued(message.Id, conversationId));
        return message;
    }

    /// <summary>
    /// The Graph API is about to be called (<see cref="MessageStatus.Queued"/> only). Saved before the call: when its
    /// outcome is then lost (crash, database failure), the next attempt finds the message
    /// <see cref="MessageStatus.Sending"/> and gives up rather than sending it twice; and of two concurrent senders, or
    /// a sender and a takeover cancelling the message, only one wins on the row version.
    /// </summary>
    public void MarkSending()
    {
        if (Direction != MessageDirection.Outbound || Status != MessageStatus.Queued)
        {
            throw new InvalidOperationException($"Only a queued outbound message can be sent (status {Status}).");
        }

        Status = MessageStatus.Sending;
    }

    /// <summary>The call certainly did not deliver the message (rate limit, service unavailable): queued again for a later attempt.</summary>
    public void ReturnToQueue()
    {
        if (Direction != MessageDirection.Outbound || Status != MessageStatus.Sending)
        {
            throw new InvalidOperationException($"Only a message being sent returns to the queue (status {Status}).");
        }

        Status = MessageStatus.Queued;
    }

    /// <summary>The Graph API accepted the message and gave its Meta id (<see cref="MessageStatus.Sending"/> only).</summary>
    public void MarkAccepted(string metaMessageId, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(metaMessageId);
        if (Direction != MessageDirection.Outbound || Status != MessageStatus.Sending)
        {
            throw new InvalidOperationException($"Only a message being sent can be accepted (status {Status}).");
        }

        MetaMessageId = MessageTexts.Truncate(metaMessageId.Trim(), MetaMessageIdMaxLength);
        Status = MessageStatus.Accepted;
        AcceptedAtUtc = now;
    }

    /// <summary>
    /// The message could not be sent: WhatsApp not configured (still queued), refused by Meta, service unavailable or
    /// outcome unknown (being sent).
    /// </summary>
    public void MarkNotSent(Error reason, int? metaErrorCode, string? metaErrorTitle, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(reason);
        if (Direction != MessageDirection.Outbound || Status is not (MessageStatus.Queued or MessageStatus.Sending))
        {
            throw new InvalidOperationException($"Only a queued outbound message can fail to be sent (status {Status}).");
        }

        Status = MessageStatus.Failed;
        FailedAtUtc = now;
        FailureCode = reason.Code;
        MetaErrorCode = metaErrorCode;
        MetaErrorTitle = metaErrorTitle is { } title ? MessageTexts.Truncate(title, MetaErrorTitleMaxLength) : null;
    }

    /// <summary>The message will never be sent (the bot was silenced, the 24-hour window closed, the consent withdrawn): queued only.</summary>
    public void Cancel(Error reason, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(reason);
        if (Direction != MessageDirection.Outbound || Status != MessageStatus.Queued)
        {
            throw new InvalidOperationException($"Only a queued outbound message can be cancelled (status {Status}).");
        }

        Status = MessageStatus.Cancelled;
        FailedAtUtc = now;
        FailureCode = reason.Code;
    }

    /// <summary>
    /// Records a status reported by Meta (<see cref="MessageStatus.Sent"/>, <see cref="MessageStatus.Delivered"/>,
    /// <see cref="MessageStatus.Read"/> or <see cref="MessageStatus.Failed"/>) with its pricing information. Each status is
    /// kept once: returns <see langword="false"/> for a duplicate. Statuses may arrive out of order; the current status
    /// is the most advanced one (sent &lt; failed &lt; delivered &lt; read).
    /// </summary>
    public bool RecordStatus(
        MessageStatus status,
        DateTimeOffset occurredAt,
        int? errorCode,
        string? errorTitle,
        MessagePricing? pricing,
        DateTimeOffset now)
    {
        if (status is not (MessageStatus.Sent or MessageStatus.Delivered or MessageStatus.Read or MessageStatus.Failed))
        {
            throw new ArgumentOutOfRangeException(nameof(status), status, "Meta reports sent, delivered, read or failed.");
        }

        if (Direction != MessageDirection.Outbound || Status is MessageStatus.Queued or MessageStatus.Sending or MessageStatus.Cancelled
            || _statusChanges.Exists(change => change.Status == status))
        {
            return false;
        }

        var at = occurredAt.ToUniversalTime();
        var title = errorTitle is { } text ? MessageTexts.Truncate(text, MetaErrorTitleMaxLength) : null;
        _statusChanges.Add(MessageStatusChange.Record(Id, status, at, status == MessageStatus.Failed ? errorCode : null, status == MessageStatus.Failed ? title : null, now));
        switch (status)
        {
            case MessageStatus.Sent:
                SentAtUtc = at;
                break;
            case MessageStatus.Delivered:
                DeliveredAtUtc = at;
                break;
            case MessageStatus.Read:
                ReadAtUtc = at;
                break;
            default:
                FailedAtUtc = at;
                FailureCode = MessagingErrors.SendRejected.Code;
                MetaErrorCode = errorCode;
                MetaErrorTitle = title;
                break;
        }

        if (Rank(status) > Rank(Status))
        {
            Status = status;
        }

        if (pricing is not null)
        {
            PricingCategory = pricing.Category is { } category ? MessageTexts.Truncate(category, PricingFieldMaxLength) : PricingCategory;
            IsBillable = pricing.Billable ?? IsBillable;
            PricingModel = pricing.Model is { } model ? MessageTexts.Truncate(model, PricingFieldMaxLength) : PricingModel;
        }

        return true;
    }

    /// <summary>Records what the auto-responder did with this customer message (a pending turn only; later calls are ignored).</summary>
    public bool CompleteBotTurn(BotTurnState outcome)
    {
        if (outcome is BotTurnState.None or BotTurnState.Pending || !Enum.IsDefined(outcome))
        {
            throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "A turn ends replied, without reply, superseded or silenced.");
        }

        if (BotTurn != BotTurnState.Pending)
        {
            return false;
        }

        BotTurn = outcome;
        return true;
    }

    private static int Rank(MessageStatus status) => status switch
    {
        MessageStatus.Accepted => 1,
        MessageStatus.Sent => 2,
        MessageStatus.Failed => 3,
        MessageStatus.Delivered => 4,
        MessageStatus.Read => 5,
        _ => 0,
    };
}

/// <summary>One delivery status of an outbound message, as reported by Meta (each status kept once). Part of <see cref="Message"/>.</summary>
public sealed class MessageStatusChange : Entity
{
    private MessageStatusChange()
    {
    }

    private MessageStatusChange(Guid id, Guid messageId, MessageStatus status, DateTimeOffset occurredAtUtc, int? errorCode, string? errorTitle, DateTimeOffset recordedAtUtc)
        : base(id)
    {
        MessageId = messageId;
        Status = status;
        OccurredAtUtc = occurredAtUtc;
        ErrorCode = errorCode;
        ErrorTitle = errorTitle;
        RecordedAtUtc = recordedAtUtc;
    }

    public Guid MessageId { get; private set; }

    public MessageStatus Status { get; private set; }

    /// <summary>When Meta says it happened.</summary>
    public DateTimeOffset OccurredAtUtc { get; private set; }

    public int? ErrorCode { get; private set; }

    public string? ErrorTitle { get; private set; }

    /// <summary>When the platform processed the status.</summary>
    public DateTimeOffset RecordedAtUtc { get; private set; }

    internal static MessageStatusChange Record(Guid messageId, MessageStatus status, DateTimeOffset occurredAt, int? errorCode, string? errorTitle, DateTimeOffset now) =>
        new(NewId(), messageId, status, occurredAt, errorCode, errorTitle, now);
}

/// <summary>Pricing information of a status (Meta <c>pricing</c> object): category, billable flag and pricing model, as reported.</summary>
public sealed record MessagePricing(string? Category, bool? Billable, string? Model);
