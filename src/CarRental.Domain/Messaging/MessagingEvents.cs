using CarRental.Domain.Common;

namespace CarRental.Domain.Messaging;

// Events carry identifiers only: they are stored in the outbox, which is not masked like the audit log.

public sealed record ConversationStarted(Guid ConversationId) : IDomainEvent;

/// <summary>
/// A customer message arrived while the bot answers the conversation: the outbox runs one auto-responder turn for it
/// (skipped when a newer customer message or a takeover came first).
/// </summary>
public sealed record CustomerMessageReceived(Guid ConversationId, Guid MessageId) : IDomainEvent;

/// <summary>An outbound message (bot, agent or notification) is waiting to be sent through the Graph API by the outbox.</summary>
public sealed record OutboundMessageQueued(Guid MessageId, Guid ConversationId) : IDomainEvent;

/// <summary>The conversation went to <paramref name="Mode"/> (takeover, hand-over by the bot, hand-back).</summary>
public sealed record ConversationModeChanged(Guid ConversationId, ConversationMode Mode, Guid? AgentId) : IDomainEvent;
