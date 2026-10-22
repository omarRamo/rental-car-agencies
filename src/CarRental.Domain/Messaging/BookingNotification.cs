using CarRental.Domain.Common;

namespace CarRental.Domain.Messaging;

/// <summary>
/// Proof that a utility notification of a rental file was queued (its template message), unique per file and kind: a
/// replayed outbox message or a second run of a reminder job never sends it twice. It carries the agency of the file,
/// which is the agency of the conversation it went through. Ids only, no personal data.
/// </summary>
public sealed class BookingNotification : Entity, IAgencyOwned
{
    private BookingNotification()
    {
    }

    private BookingNotification(Guid id, Guid agencyId, Guid bookingId, NotificationKind kind, Guid conversationId, Guid messageId, Guid personId, DateTimeOffset now)
        : base(id)
    {
        AgencyId = agencyId;
        BookingId = bookingId;
        Kind = kind;
        ConversationId = conversationId;
        MessageId = messageId;
        PersonId = personId;
        CreatedAtUtc = now;
    }

    /// <summary>The agency of the rental file.</summary>
    public Guid AgencyId { get; private set; }

    public Guid BookingId { get; private set; }

    public NotificationKind Kind { get; private set; }

    public Guid ConversationId { get; private set; }

    /// <summary>The template message queued for the customer.</summary>
    public Guid MessageId { get; private set; }

    /// <summary>The person notified (main driver of the file), who had given the WhatsApp notifications consent.</summary>
    public Guid PersonId { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    /// <summary>
    /// The notification of the file <paramref name="bookingId"/> of the agency <paramref name="bookingAgencyId"/>, sent
    /// through the conversation of that agency.
    /// </summary>
    public static BookingNotification Record(
        Guid bookingId,
        Guid bookingAgencyId,
        NotificationKind kind,
        Conversation conversation,
        Message message,
        Guid personId,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        ArgumentNullException.ThrowIfNull(message);
        if (bookingId == Guid.Empty || personId == Guid.Empty || !Enum.IsDefined(kind))
        {
            throw new ArgumentException("A notification needs its file, its kind and its person.", nameof(bookingId));
        }

        if (conversation.AgencyId != bookingAgencyId)
        {
            throw new ArgumentException("A file is notified through a conversation of its own agency.", nameof(conversation));
        }

        if (message.ConversationId != conversation.Id || message.Author != MessageAuthor.System)
        {
            throw new ArgumentException("The message must be a notification of the conversation.", nameof(message));
        }

        return new BookingNotification(NewId(), bookingAgencyId, bookingId, kind, conversation.Id, message.Id, personId, now);
    }
}
