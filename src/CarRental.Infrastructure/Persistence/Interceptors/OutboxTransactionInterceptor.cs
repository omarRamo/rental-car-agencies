using System.Data.Common;
using CarRental.Infrastructure.Outbox;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace CarRental.Infrastructure.Persistence.Interceptors;

/// <summary>Triggers the outbox processor once the transaction that wrote outbox messages has committed.</summary>
internal sealed class OutboxTransactionInterceptor(OutboxDispatchSignal signal, IOutboxDispatcher dispatcher) : DbTransactionInterceptor
{
    public override void TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData)
    {
        if (signal.TryConsume())
        {
            dispatcher.TriggerProcessing();
        }

        base.TransactionCommitted(transaction, eventData);
    }

    public override Task TransactionCommittedAsync(
        DbTransaction transaction,
        TransactionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        if (signal.TryConsume())
        {
            dispatcher.TriggerProcessing();
        }

        return base.TransactionCommittedAsync(transaction, eventData, cancellationToken);
    }

    public override void TransactionRolledBack(DbTransaction transaction, TransactionEndEventData eventData)
    {
        signal.Reset();
        base.TransactionRolledBack(transaction, eventData);
    }

    public override Task TransactionRolledBackAsync(
        DbTransaction transaction,
        TransactionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        signal.Reset();
        return base.TransactionRolledBackAsync(transaction, eventData, cancellationToken);
    }
}
