using CarRental.Application.Common.Outbox;
using CarRental.Application.Messaging.Contracts;
using CarRental.Domain.Messaging;
using Microsoft.Extensions.Logging;

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
/// One bot turn per customer message received in bot mode (<see cref="CustomerMessageReceived"/>, through the outbox):
/// prepares the turn, calls the responder in a scope of its own, then applies its reply on fresh data. At-least-once
/// delivery is harmless: a message already answered (or superseded, or silenced) produces no turn.
/// </summary>
internal sealed partial class ConversationTurnHandler(MessagingScopes scopes, ILogger<ConversationTurnHandler> logger)
    : IOutboxHandler<CustomerMessageReceived>
{
    public async Task HandleAsync(CustomerMessageReceived message, OutboxMessageContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(context);
        var turn = await scopes.RunAsync<ConversationTurns, ConversationTurn?>(
            (turns, ct) => turns.PrepareAsync(message.ConversationId, message.MessageId, ct),
            context.CorrelationId,
            cancellationToken);
        if (turn is null)
        {
            return;
        }

        var reply = await scopes.RunOnceAsync<IConversationResponder, ConversationReply>(
            (responder, ct) => responder.RespondAsync(turn, ct),
            context.CorrelationId,
            cancellationToken) ?? ConversationReply.None;

        var replied = await scopes.RunAsync<ConversationTurns, bool>(
            (turns, ct) => turns.ApplyAsync(turn, reply, ct),
            context.CorrelationId,
            cancellationToken);
        LogTurn(turn.NewMessages.Count, replied, reply.HandOverReason is not null);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "WhatsApp bot turn of {Messages} customer message(s): replied {Replied}, hand-over {HandOver}.")]
    private partial void LogTurn(int messages, bool replied, bool handOver);
}
