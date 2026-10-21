using System.Data.Common;
using CarRental.Infrastructure.Outbox;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace CarRental.Infrastructure.Persistence.Interceptors;

/// <summary>
/// Once a transaction has committed: tells the cache listeners which entity types it changed (ADR-0044) and triggers the
/// outbox processor when it wrote outbox messages. A rollback discards both.
/// </summary>
internal sealed class OutboxTransactionInterceptor(OutboxDispatchSignal signal, IOutboxDispatcher dispatcher, CommittedChanges committedChanges) : DbTransactionInterceptor
{
    public override void TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData)
    {
        committedChanges.Flush();
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
        committedChanges.Flush();
        if (signal.TryConsume())
        {
            dispatcher.TriggerProcessing();
        }

        return base.TransactionCommittedAsync(transaction, eventData, cancellationToken);
    }

    public override void TransactionRolledBack(DbTransaction transaction, TransactionEndEventData eventData)
    {
        signal.Reset();
        committedChanges.Reset();
        base.TransactionRolledBack(transaction, eventData);
    }

    public override Task TransactionRolledBackAsync(
        DbTransaction transaction,
        TransactionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        signal.Reset();
        committedChanges.Reset();
        return base.TransactionRolledBackAsync(transaction, eventData, cancellationToken);
    }
}
