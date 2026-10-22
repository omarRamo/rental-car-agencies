namespace CarRental.Application.Messaging.Contracts;

/// <summary>
/// Public read API of the Messaging module for other modules (ids and contract DTOs only): the WhatsApp conversations
/// and their messages, of the agency of the operation (ADR-0067). No permission check here: the caller enforces its own
/// (the shared inbox requires <c>whatsapp.inbox</c>; jobs run as the system actor).
/// </summary>
public interface IMessagingQueries
{
    /// <summary>The conversation, or <see langword="null"/>.</summary>
    Task<ConversationInfo?> GetConversationAsync(Guid conversationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The conversation of <paramref name="agencyId"/> with a WhatsApp number (E.164 or Meta <c>wa_id</c>), or
    /// <see langword="null"/>: a number has one conversation per agency.
    /// </summary>
    Task<ConversationInfo?> FindByNumberAsync(Guid agencyId, string whatsAppNumber, CancellationToken cancellationToken = default);

    /// <summary>The conversations linked to a person, the most recent activity first.</summary>
    Task<IReadOnlyList<ConversationInfo>> GetConversationsOfPersonAsync(Guid personId, CancellationToken cancellationToken = default);

    /// <summary>The last <paramref name="limit"/> messages of a conversation (1 to 200), oldest first.</summary>
    Task<IReadOnlyList<ConversationMessageInfo>> GetMessagesAsync(Guid conversationId, int limit = 50, CancellationToken cancellationToken = default);
}
