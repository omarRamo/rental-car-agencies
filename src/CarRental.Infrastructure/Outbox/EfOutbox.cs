using CarRental.Application.Common.Outbox;
using CarRental.Application.Common.Security;
using CarRental.Infrastructure.Persistence;

namespace CarRental.Infrastructure.Outbox;

internal sealed class EfOutbox(CarRentalDbContext db, OutboxSerializer serializer, TimeProvider timeProvider, IUserContext userContext) : IOutbox
{
    public void Enqueue<TMessage>(TMessage message)
        where TMessage : class
    {
        ArgumentNullException.ThrowIfNull(message);
        db.Set<OutboxMessage>().Add(OutboxMessage.Create(
            message.GetType().FullName!,
            serializer.Serialize(message),
            timeProvider.GetUtcNow(),
            userContext.CorrelationId));
    }
}
