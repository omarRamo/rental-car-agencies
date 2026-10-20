using System.Collections;
using System.Linq.Expressions;
using CarRental.Application.Common.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;

namespace CarRental.Application.Tests.Fleet.Fakes;

/// <summary>
/// An <see cref="IAppDbContext"/> over in-memory lists, with the asynchronous LINQ operators of EF Core (LINQ to
/// objects underneath; <c>Include</c> and <c>AsNoTracking</c> are no-ops). Aggregates keep their children in memory,
/// so queries going through the roots (<c>SelectMany(v =&gt; v.Documents)</c>) see them. SQL semantics are covered by
/// the integration tests.
/// </summary>
internal sealed class FakeAppDbContext : IAppDbContext
{
    private readonly Dictionary<Type, object> _sets = [];

    public int Saves { get; private set; }

    public DbSet<TEntity> Set<TEntity>()
        where TEntity : class => Fake<TEntity>();

    public FakeDbSet<TEntity> Fake<TEntity>()
        where TEntity : class
    {
        if (!_sets.TryGetValue(typeof(TEntity), out var set))
        {
            set = new FakeDbSet<TEntity>();
            _sets[typeof(TEntity)] = set;
        }

        return (FakeDbSet<TEntity>)set;
    }

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        Saves++;
        return Task.FromResult(0);
    }
}

internal sealed class FakeDbSet<TEntity> : DbSet<TEntity>, IQueryable<TEntity>, IAsyncEnumerable<TEntity>
    where TEntity : class
{
    private readonly List<TEntity> _items = [];

    public IReadOnlyList<TEntity> Items => _items;

    public override IEntityType EntityType => throw new NotSupportedException("The fake context has no model.");

    Type IQueryable.ElementType => typeof(TEntity);

    Expression IQueryable.Expression => ((IQueryable)Query).Expression;

    IQueryProvider IQueryable.Provider => ((IQueryable)Query).Provider;

    private FakeAsyncEnumerable<TEntity> Query => new(_items);

    public override EntityEntry<TEntity> Add(TEntity entity)
    {
        _items.Add(entity);
        return null!;
    }

    public override EntityEntry<TEntity> Remove(TEntity entity)
    {
        _items.Remove(entity);
        return null!;
    }

    public override IAsyncEnumerator<TEntity> GetAsyncEnumerator(CancellationToken cancellationToken = default) =>
        new FakeAsyncEnumerator<TEntity>(_items.ToList().GetEnumerator());

    IEnumerator<TEntity> IEnumerable<TEntity>.GetEnumerator() => _items.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => _items.GetEnumerator();
}

internal sealed class FakeAsyncEnumerable<T> : EnumerableQuery<T>, IAsyncEnumerable<T>, IQueryable<T>
{
    public FakeAsyncEnumerable(IEnumerable<T> enumerable)
        : base(enumerable)
    {
    }

    public FakeAsyncEnumerable(Expression expression)
        : base(expression)
    {
    }

    IQueryProvider IQueryable.Provider => new FakeAsyncQueryProvider(this);

    public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default) =>
        new FakeAsyncEnumerator<T>(this.AsEnumerable().GetEnumerator());
}

internal sealed class FakeAsyncQueryProvider(IQueryProvider inner) : IAsyncQueryProvider
{
    public IQueryable CreateQuery(Expression expression)
    {
        var elementType = expression.Type.GetInterfaces().Append(expression.Type)
            .First(t => t.IsGenericType && t.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            .GetGenericArguments()[0];
        return (IQueryable)Activator.CreateInstance(typeof(FakeAsyncEnumerable<>).MakeGenericType(elementType), expression)!;
    }

    public IQueryable<TElement> CreateQuery<TElement>(Expression expression) => new FakeAsyncEnumerable<TElement>(expression);

    public object? Execute(Expression expression) => inner.Execute(expression);

    public TResult Execute<TResult>(Expression expression) => inner.Execute<TResult>(expression);

    public TResult ExecuteAsync<TResult>(Expression expression, CancellationToken cancellationToken = default)
    {
        var resultType = typeof(TResult).GetGenericArguments()[0];
        var result = typeof(IQueryProvider)
            .GetMethod(nameof(IQueryProvider.Execute), 1, [typeof(Expression)])!
            .MakeGenericMethod(resultType)
            .Invoke(this, [expression]);
        return (TResult)typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(resultType).Invoke(null, [result])!;
    }
}

internal sealed class FakeAsyncEnumerator<T>(IEnumerator<T> inner) : IAsyncEnumerator<T>
{
    public T Current => inner.Current;

    public ValueTask<bool> MoveNextAsync() => ValueTask.FromResult(inner.MoveNext());

    public ValueTask DisposeAsync()
    {
        inner.Dispose();
        return ValueTask.CompletedTask;
    }
}
