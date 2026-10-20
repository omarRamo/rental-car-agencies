using System.Collections;
using System.Linq.Expressions;
using System.Reflection;
using CarRental.Application.Common.Persistence;
using CarRental.Domain.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;

namespace CarRental.Application.Tests.Customers.Fakes;

/// <summary>
/// An <see cref="IAppDbContext"/> over in-memory lists that supports the EF Core async LINQ operators used by the
/// services (LINQ to objects underneath, no provider). Entities are shared references, as if always tracked. A save
/// moves the domain events of the aggregates to <see cref="PublishedEvents"/>, like the outbox interceptor.
/// </summary>
internal sealed class InMemoryAppDbContext : IAppDbContext
{
    private readonly Dictionary<Type, object> _sets = [];

    public int Saves { get; private set; }

    public List<IDomainEvent> PublishedEvents { get; } = [];

    public List<TEntity> Rows<TEntity>()
        where TEntity : class => ((InMemoryDbSet<TEntity>)Set<TEntity>()).Items;

    public DbSet<TEntity> Set<TEntity>()
        where TEntity : class
    {
        if (!_sets.TryGetValue(typeof(TEntity), out var set))
        {
            set = new InMemoryDbSet<TEntity>([]);
            _sets.Add(typeof(TEntity), set);
        }

        return (DbSet<TEntity>)set;
    }

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        Saves++;
        foreach (var aggregate in _sets.Values.SelectMany(set => ((IEnumerable)set).OfType<AggregateRoot>()).ToList())
        {
            PublishedEvents.AddRange(aggregate.DequeueDomainEvents());
        }

        return Task.FromResult(0);
    }
}

internal sealed class InMemoryDbSet<TEntity>(List<TEntity> items) : DbSet<TEntity>, IQueryable<TEntity>, IAsyncEnumerable<TEntity>
    where TEntity : class
{
    private readonly InMemoryQueryable<TEntity> _query = new(items);

    public List<TEntity> Items { get; } = items;

    public override IEntityType EntityType => throw new NotSupportedException("No model in the in-memory test context.");

    Type IQueryable.ElementType => typeof(TEntity);

    Expression IQueryable.Expression => ((IQueryable)_query).Expression;

    IQueryProvider IQueryable.Provider => ((IQueryable)_query).Provider;

    public override EntityEntry<TEntity> Add(TEntity entity)
    {
        Items.Add(entity);
        return null!;
    }

    public override EntityEntry<TEntity> Remove(TEntity entity)
    {
        Items.Remove(entity);
        return null!;
    }

    public override void AddRange(params TEntity[] entities) => Items.AddRange(entities);

    public override IAsyncEnumerable<TEntity> AsAsyncEnumerable() => _query;

    public override IQueryable<TEntity> AsQueryable() => _query;

    IEnumerator<TEntity> IEnumerable<TEntity>.GetEnumerator() => Items.ToList().GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => Items.ToList().GetEnumerator();

    IAsyncEnumerator<TEntity> IAsyncEnumerable<TEntity>.GetAsyncEnumerator(CancellationToken cancellationToken) =>
        _query.GetAsyncEnumerator(cancellationToken);
}

internal sealed class InMemoryQueryable<T> : EnumerableQuery<T>, IAsyncEnumerable<T>, IQueryable<T>
{
    public InMemoryQueryable(IEnumerable<T> items)
        : base(items)
    {
    }

    public InMemoryQueryable(Expression expression)
        : base(expression)
    {
    }

    IQueryProvider IQueryable.Provider => new InMemoryQueryProvider(this);

    public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default) =>
        new InMemoryAsyncEnumerator<T>(this.AsEnumerable().ToList().GetEnumerator());
}

internal sealed class InMemoryQueryProvider(IQueryProvider inner) : IAsyncQueryProvider
{
    private static readonly MethodInfo ExecuteMethod =
        typeof(IQueryProvider).GetMethod(nameof(IQueryProvider.Execute), 1, [typeof(Expression)])!;

    private static readonly MethodInfo FromResultMethod = typeof(Task).GetMethod(nameof(Task.FromResult))!;

    public IQueryable CreateQuery(Expression expression)
    {
        var elementType = expression.Type.GetInterfaces()
            .Append(expression.Type)
            .First(t => t.IsGenericType && t.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            .GetGenericArguments()[0];
        return (IQueryable)Activator.CreateInstance(typeof(InMemoryQueryable<>).MakeGenericType(elementType), expression)!;
    }

    public IQueryable<TElement> CreateQuery<TElement>(Expression expression) => new InMemoryQueryable<TElement>(expression);

    public object? Execute(Expression expression) => inner.Execute(expression);

    public TResult Execute<TResult>(Expression expression) => inner.Execute<TResult>(expression);

    public TResult ExecuteAsync<TResult>(Expression expression, CancellationToken cancellationToken = default)
    {
        // TResult is Task<T>: run the query synchronously and wrap its result.
        var resultType = typeof(TResult).GetGenericArguments()[0];
        var result = ExecuteMethod.MakeGenericMethod(resultType).Invoke(inner, [expression]);
        return (TResult)FromResultMethod.MakeGenericMethod(resultType).Invoke(null, [result])!;
    }
}

internal sealed class InMemoryAsyncEnumerator<T>(IEnumerator<T> inner) : IAsyncEnumerator<T>
{
    public T Current => inner.Current;

    public ValueTask<bool> MoveNextAsync() => ValueTask.FromResult(inner.MoveNext());

    public ValueTask DisposeAsync()
    {
        inner.Dispose();
        return ValueTask.CompletedTask;
    }
}
