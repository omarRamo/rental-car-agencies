namespace CarRental.Application.Common.Outbox;

/// <summary>
/// Adds an application message to the outbox of the current unit of work. It is written by the next
/// <c>SaveChangesAsync</c>, atomically with the other changes, then dispatched to its handlers by the outbox processor.
/// </summary>
/// <remarks>Domain events raised by aggregates are written to the outbox automatically; use this port for other messages.</remarks>
public interface IOutbox
{
    void Enqueue<TMessage>(TMessage message)
        where TMessage : class;
}
