namespace CarRental.Application.Common.Caching;

/// <summary>
/// Told after a database commit which entity types were inserted, updated or deleted, whatever wrote them (a service,
/// a seeder, a test), so that in-memory caches of rarely changing data (settings, published rate plans) drop their
/// snapshot (ADR-0044). Never called for a rolled-back transaction.
/// </summary>
/// <remarks>Implementations are singletons; they must be fast, thread-safe and never throw.</remarks>
public interface ICommittedChangesListener
{
    void OnCommitted(IReadOnlySet<Type> changedEntityTypes);
}
