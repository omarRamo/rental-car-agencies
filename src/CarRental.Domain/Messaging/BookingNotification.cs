using CarRental.Domain.Common;

namespace CarRental.Domain.Messaging;

/// <summary>
/// Proof that a utility notification of a rental file was queued (its template message), unique per file and kind: a
/// replayed outbox message or a second run of a reminder job never sends it twice. Ids only, no personal data.
/// </summary>
public sealed class BookingNotification : Entity
{
    private BookingNotification()
    {
    }

    private BookingNotification(Guid id, Guid bookingId, NotificationKind kind, Guid conversationId, Guid messageId, Guid personId, DateTimeOffset now)
        : base(id)
    {
        BookingId = bookingId;
        Kind = kind;
        ConversationId = conversationId;
        MessageId = messageId;
        PersonId = personId;
        CreatedAtUtc = now;
    }

    public Guid BookingId { get; private set; }

    public NotificationKind Kind { get; private set; }

    public Guid ConversationId { get; private set; }

    /// <summary>The template message queued for the customer.</summary>
    public Guid MessageId { get; private set; }

    /// <summary>The person notified (main driver of the file), who had given the WhatsApp notifications consent.</summary>
    public Guid PersonId { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public static BookingNotification Record(Guid bookingId, NotificationKind kind, Conversation conversation, Message message, Guid personId, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        ArgumentNullException.ThrowIfNull(message);
        if (bookingId == Guid.Empty || personId == Guid.Empty || !Enum.IsDefined(kind))
        {
            throw new ArgumentException("A notification needs its file, its kind and its person.", nameof(bookingId));
        }

        if (message.ConversationId != conversation.Id || message.Author != MessageAuthor.System)
        {
            throw new ArgumentException("The message must be a notification of the conversation.", nameof(message));
        }

        return new BookingNotification(NewId(), bookingId, kind, conversation.Id, message.Id, personId, now);
    }
}
