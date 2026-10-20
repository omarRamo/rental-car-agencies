namespace CarRental.Application.Common.Outbox;

/// <summary>
/// Handles an outbox message (a domain event or an application message such as <c>EmailRequested</c>)
/// asynchronously, after the transaction that produced it has committed.
/// </summary>
/// <remarks>
/// Delivery is at-least-once: a handler can run again for the same message (retry after a failure of this or
/// another handler of the message), so it must be idempotent, e.g. keyed on <see cref="OutboxMessageContext.MessageId"/>.
/// Each message is processed in its own DI scope with the system actor (<c>system:outbox</c>).
/// Register handlers with <see cref="OutboxServiceCollectionExtensions.AddOutboxHandler{TMessage, THandler}"/>.
/// </remarks>
public interface IOutboxHandler<in TMessage>
    where TMessage : class
{
    Task HandleAsync(TMessage message, OutboxMessageContext context, CancellationToken cancellationToken);
}

/// <summary>Metadata of the outbox message being handled.</summary>
/// <param name="MessageId">Unique id of the outbox message (use it as idempotency key).</param>
/// <param name="OccurredAtUtc">When the message was written.</param>
/// <param name="Attempt">1 for the first attempt.</param>
/// <param name="CorrelationId">Correlation id of the operation that produced the message.</param>
public sealed record OutboxMessageContext(Guid MessageId, DateTimeOffset OccurredAtUtc, int Attempt, string? CorrelationId);
