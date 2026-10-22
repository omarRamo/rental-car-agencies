namespace CarRental.Application.Messaging.Contracts;

/// <summary>
/// Extension point of the WhatsApp auto-responder (package 4B implements it with the AI agent; the default answers
/// nothing). The platform calls it at most once per customer turn of a conversation in bot mode, in a DI scope of its
/// own with the system actor working for the agency of the conversation only (ADR-0067: everything the responder reads
/// and writes is of that agency), outside any database transaction, and applies what it returns:
/// <list type="number">
/// <item>the usage of the turn is added to the counters of the conversation (caps of the bot);</item>
/// <item>the need replaces the structured need of the conversation;</item>
/// <item>the message — at most one per turn (Meta bills each service message) — is queued and sent, if the conversation
/// is still in bot mode and its 24-hour window is open;</item>
/// <item>a hand-over reason switches the conversation to human mode after that message: the bot is silenced until an
/// agent hands it back.</item>
/// </list>
/// </summary>
/// <remarks>
/// <para>A turn groups the customer messages not answered yet (<see cref="ConversationTurn.NewMessages"/>): messages
/// received in a burst are answered once, by the turn of the latest one. A takeover by an agent, before the call or
/// during it, silences the turn: its message is dropped (even if the agent hands the conversation back before the reply
/// comes), its usage still counts.</para>
/// <para>Return <see cref="ConversationReply.None"/> to stay silent. Business problems (caps reached, model refusal,
/// tool failure) are answered with a hand-over, never with an exception: an exception retries the whole turn later
/// through the outbox (the responder may then be called again for the same turn).</para>
/// <para>Register an implementation in <c>AddMessagingModule()</c> in place of the default one
/// (<c>services.Replace(ServiceDescriptor.Scoped&lt;IConversationResponder, T&gt;())</c>).</para>
/// </remarks>
public interface IConversationResponder
{
    Task<ConversationReply> RespondAsync(ConversationTurn turn, CancellationToken cancellationToken);
}

/// <summary>
/// One turn of a conversation in bot mode: the agency the customer writes to, who writes (number, profile name, linked
/// person, payer and rental file), the time, the end of the 24-hour window, the need and usage so far, the customer
/// messages of this turn and the earlier history (both oldest first; the history holds at most
/// <see cref="MaxHistory"/> messages).
/// </summary>
/// <param name="ConversationId">The conversation.</param>
/// <param name="AgencyId">The agency of the conversation: the responder only answers about it (places, prices, files, knowledge).</param>
/// <param name="TriggerMessageId">The latest customer message, which triggered the turn.</param>
/// <param name="WhatsAppNumber">The customer's number in E.164, e.g. <c>+21698123456</c> (check it against a rental file before giving its status).</param>
/// <param name="ProfileName">The customer's WhatsApp profile name, if any.</param>
/// <param name="PersonId">The person linked to the conversation (Customers module), if any.</param>
/// <param name="CustomerId">The payer linked to the conversation, if any.</param>
/// <param name="BookingId">The rental file linked to the conversation (Bookings module), if any.</param>
/// <param name="NowUtc">Time of the turn.</param>
/// <param name="WindowClosesAtUtc">Free-form replies are possible until then (24 hours after the customer's last message).</param>
/// <param name="Need">The structured need so far.</param>
/// <param name="Usage">The usage of the bot so far on this conversation.</param>
/// <param name="NewMessages">The customer messages of this turn (not answered yet), the trigger last.</param>
/// <param name="History">The earlier messages of the conversation, both directions, oldest first.</param>
public sealed record ConversationTurn(
    Guid ConversationId,
    Guid AgencyId,
    Guid TriggerMessageId,
    string WhatsAppNumber,
    string? ProfileName,
    Guid? PersonId,
    Guid? CustomerId,
    Guid? BookingId,
    DateTimeOffset NowUtc,
    DateTimeOffset WindowClosesAtUtc,
    ConversationNeedInfo Need,
    ConversationUsageInfo Usage,
    IReadOnlyList<ConversationMessageInfo> NewMessages,
    IReadOnlyList<ConversationMessageInfo> History)
{
    /// <summary>Most messages of <see cref="History"/>.</summary>
    public const int MaxHistory = 50;
}

/// <summary>What the auto-responder decided for a turn; every part is optional.</summary>
public sealed record ConversationReply
{
    /// <summary>Stay silent: no message, need and usage unchanged.</summary>
    public static ConversationReply None { get; } = new();

    /// <summary>The single message of the turn (text, buttons or list; templates are not for the bot).</summary>
    public OutboundMessageContent? Message { get; init; }

    /// <summary>The updated need (replaces the current one); <see langword="null"/> keeps it.</summary>
    public ConversationNeedInfo? Need { get; init; }

    /// <summary>The usage of the turn (model calls); <see langword="null"/> when no model was called.</summary>
    public ConversationTurnUsageInfo? Usage { get; init; }

    /// <summary>
    /// Hands the conversation over to a human with this reason (e.g. tool <c>transferer_a_un_agent</c>, caps reached): the
    /// bot is silenced once <see cref="Message"/> (e.g. "un agent vous répond bientôt") is queued.
    /// </summary>
    public string? HandOverReason { get; init; }
}

/// <summary>Usage of one turn: tokens (input, output, cache read and write), tool calls, AI cost and estimated Meta cost of the reply in USD.</summary>
public sealed record ConversationTurnUsageInfo(
    int InputTokens,
    int OutputTokens,
    int CacheReadTokens = 0,
    int CacheWriteTokens = 0,
    int ToolCalls = 0,
    decimal AiCostUsd = 0m,
    decimal MetaCostUsd = 0m);
