using CarRental.Application.Common.Outbox;
using CarRental.Application.Messaging.Agent.Turns;
using CarRental.Application.Messaging.Contracts;
using CarRental.Domain.Messaging;

namespace CarRental.Application.Messaging.Responder;

/// <summary>
/// The default <see cref="IConversationResponder"/> until the AI agent (package 4B) replaces it: it never answers, so
/// customer messages simply wait for an agent in the shared inbox.
/// </summary>
internal sealed class NoReplyResponder : IConversationResponder
{
    public Task<ConversationReply> RespondAsync(ConversationTurn turn, CancellationToken cancellationToken) =>
        Task.FromResult(ConversationReply.None);
}

/// <summary>
/// One bot turn per customer message received in bot mode (<see cref="CustomerMessageReceived"/>, through the outbox),
/// handed to the <see cref="IBotTurnDispatcher"/>: in production the bot turn executor runs it in the background, outside
/// the outbox (model calls take seconds and must not hold up the other side effects, ADR-0054); without the executor the
/// turn runs here (prepare, respond, apply). At-least-once delivery is harmless: a message already answered (or
/// superseded, or silenced) produces no turn.
/// </summary>
internal sealed class ConversationTurnHandler(IBotTurnDispatcher dispatcher) : IOutboxHandler<CustomerMessageReceived>
{
    public Task HandleAsync(CustomerMessageReceived message, OutboxMessageContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(context);
        return dispatcher.DispatchAsync(message.ConversationId, message.MessageId, context.CorrelationId, cancellationToken);
    }
}
