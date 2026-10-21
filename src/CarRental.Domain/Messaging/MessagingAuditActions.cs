namespace CarRental.Domain.Messaging;

/// <summary>
/// Explicit audit events of the Messaging module (dotted, lower case, never renamed). Conversation changes are also
/// audited automatically (personal data masked); message contents never reach the audit log.
/// </summary>
public static class MessagingAuditActions
{
    /// <summary>An agent took a conversation over: the bot is silenced.</summary>
    public const string ConversationTakenOver = "messaging.conversation_taken_over";

    /// <summary>An agent handed a conversation back to the bot.</summary>
    public const string ConversationHandedBack = "messaging.conversation_handed_back";

    /// <summary>An agent linked (or unlinked) a conversation to a person or a rental file.</summary>
    public const string ConversationLinked = "messaging.conversation_linked";

    /// <summary>An agent sent a message (text, interactive or template) from the shared inbox; the content is not logged.</summary>
    public const string MessageSentByAgent = "messaging.message_sent_by_agent";

    /// <summary>The retention job purged messages and conversations.</summary>
    public const string ConversationsPurged = "messaging.conversations_purged";
}
